using System.Net;
using System.Text.Json;
using MailStudio.Models;

namespace MailStudio.Services.Archive;

/// <summary>
/// A local, read-only mailbox made from an imported archive (Outlook .pst / .ost). It has no server: folders,
/// mails and attachments live on this computer (folders.json, the mail cache, archives/&lt;id&gt;/files). It never sends.
/// </summary>
public static class LocalArchive
{
    public const string Host = "local-archive.mailstudio";
    public static bool IsArchive(AccountConfig a) => a.ImapHost == Host;
    /// <summary>Accounts served from local data (demo and archives): no IMAP / SMTP connection is ever made.</summary>
    public static bool IsLocal(AccountConfig a) => DemoData.IsDemo(a) || IsArchive(a);
    public static string Dir(string accountId) => Path.Combine(AppPaths.Root, "archives", accountId);

    public static List<FolderInfo> Folders(AccountConfig a)
    {
        var path = Path.Combine(Dir(a.Id), "folders.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<FolderInfo>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    internal static void SaveFolders(string accountId, List<FolderInfo> folders)
    {
        Directory.CreateDirectory(Dir(accountId));
        JsonFile.WriteAtomic(Path.Combine(Dir(accountId), "folders.json"), JsonSerializer.Serialize(folders));
    }

    /// <summary>Copies an archived attachment (its Part is the path inside the archive folder) to the attachment folder.</summary>
    public static async Task<string> CopyAttachmentAsync(AccountConfig a, AttachmentInfo att, string targetDir)
    {
        var root = Path.GetFullPath(Dir(a.Id));
        var src = Path.GetFullPath(Path.Combine(root, att.Part));
        if (!src.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(src))
            throw new FileNotFoundException(L.T("Arşivdeki ek dosyası bulunamadı."), att.FileName);
        Directory.CreateDirectory(targetDir);
        var dest = AttachmentFiles.PathFor(targetDir, att);
        await using (var i = File.OpenRead(src)) await AttachmentFiles.WriteAsync(dest, s => i.CopyToAsync(s), default);
        return dest;
    }
}

/// <summary>A mail as read from an archive file.</summary>
public sealed record ArchiveMessage(string Subject, string FromName, string FromAddress, string To, string Cc, DateTime? Date, bool IsRead,
    string? Html, string? Text, string? MessageId, IReadOnlyList<ArchiveAttachment> Attachments, int SkippedEmbedded = 0);

/// <summary>An attachment that can be written to a stream on demand.</summary>
public sealed record ArchiveAttachment(string FileName, long Size, Action<Stream> Write);

/// <summary>A folder of an archive file; messages are read lazily (large archives).</summary>
public sealed record ArchiveFolder(string Path, string Name, int Count, Func<IEnumerable<ArchiveMessage>> Messages);

public interface IMailArchiveSource : IDisposable
{
    IEnumerable<ArchiveFolder> Folders();
}

/// <summary>Result of an import.</summary>
public sealed record PstImportResult(AccountConfig Account, int Folders, int Mails, int Attachments, int Skipped, int Duplicates);

/// <summary>
/// Imports an Outlook archive into a local archive account. Re-importing the same file adds only mails that are not
/// there yet (by Message-ID, else by date + sender + subject). Attachments are written next to the archive data;
/// embedded mails inside mails are counted as skipped.
/// </summary>
public static class PstImporter
{
    public static PstImportResult Import(IMailArchiveSource source, string sourcePath, AccountStore store, MailCache cache,
        IProgress<(int Done, int Total, string Folder)>? progress = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(sourcePath);
        var acc = store.Accounts.FirstOrDefault(a => LocalArchive.IsArchive(a) && string.Equals(a.UserName, full, StringComparison.OrdinalIgnoreCase));
        if (acc == null)
        {
            acc = new AccountConfig
            {
                DisplayName = L.F("Arşiv – {0}", Path.GetFileNameWithoutExtension(full)), Email = Path.GetFileNameWithoutExtension(full) + "@archive.local",
                UserName = full, ImapHost = LocalArchive.Host, SmtpHost = LocalArchive.Host, OfflineAttachments = true,
            };
            store.AddOrUpdate(acc);
        }
        var folders = LocalArchive.Folders(acc);
        var folderList = source.Folders().Where(f => f.Count > 0).ToList();
        var total = folderList.Sum(f => f.Count);
        int done = 0, mails = 0, files = 0, skipped = 0, dupes = 0;
        var sort = 10 + folders.Count;
        foreach (var f in folderList)
        {
            ct.ThrowIfCancellationRequested();
            var name = "Archive/" + f.Path.Trim('/');
            if (!folders.Any(x => x.FullName == name)) folders.Add(new FolderInfo(name, f.Name, Glyph(f.Name), sort++));
            var existing = cache.GetHeaders(acc.Id, name, int.MaxValue);
            var keys = existing.Select(Key).ToHashSet();
            uint uid = existing.Count == 0 ? 0 : existing.Max(h => h.Uid);
            var batch = new List<(MailHeader H, MailContent C)>();
            foreach (var m in f.Messages())
            {
                ct.ThrowIfCancellationRequested();
                done++;
                if (done % 25 == 0) progress?.Report((done, total, f.Name));
                skipped += m.SkippedEmbedded;
                var date = m.Date is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)) : DateTimeOffset.MinValue;
                var probe = new MailHeader { MessageId = m.MessageId ?? "", Subject = m.Subject, FromAddress = m.FromAddress, Date = date };
                if (!keys.Add(Key(probe))) { dupes++; continue; }
                uid++;
                var atts = new List<AttachmentInfo>();
                var n = 0;
                foreach (var a in m.Attachments)
                {
                    var safe = string.Concat(a.FileName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                    if (safe.Length == 0) safe = "attachment";
                    var rel = Path.Combine("files", JsonFile.Stamp.OfText(name).Hash.ToString("x16")[..10], uid.ToString(), $"{n++}_{safe}");
                    var path = Path.Combine(LocalArchive.Dir(acc.Id), rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var fs = File.Create(path)) a.Write(fs);
                    atts.Add(new AttachmentInfo { FileName = a.FileName, Size = new FileInfo(path).Length, Part = rel });
                    files++;
                }
                var text = m.Text ?? (m.Html != null ? MailService.HtmlToText(m.Html) : "");
                var html = m.Html ?? "<pre style=\"font-family:Segoe UI,Arial;white-space:pre-wrap\">" + WebUtility.HtmlEncode(text) + "</pre>";
                var preview = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                var h = new MailHeader
                {
                    AccountId = acc.Id, Folder = name, Uid = uid, UidValidity = 1, MessageId = m.MessageId ?? "", FromName = m.FromName, FromAddress = m.FromAddress,
                    To = m.To, Subject = m.Subject, Preview = preview.Length > 200 ? preview[..200] : preview, Date = date, IsRead = m.IsRead, HasAttachments = atts.Count > 0
                };
                batch.Add((h, new MailContent { Html = html, Text = text, Cc = m.Cc, Attachments = atts }));
                mails++;
                if (batch.Count >= 200) Flush();
            }
            Flush();
            void Flush()
            {
                if (batch.Count == 0) return;
                cache.UpsertHeaders(batch.Select(b => b.H));
                foreach (var (h, c) in batch)
                {
                    cache.SaveBody(acc.Id, h.Folder, h.Uid, c);
                    if (c.Attachments.Count > 0) cache.SaveAttachments(acc.Id, h.Folder, h.Uid, c.Attachments);
                }
                batch.Clear();
            }
        }
        LocalArchive.SaveFolders(acc.Id, folders);
        progress?.Report((total, total, ""));
        return new PstImportResult(acc, folderList.Count, mails, files, skipped, dupes);
    }

    static string Key(MailHeader h) => h.MessageId.Length > 0 ? "id:" + h.MessageId.Trim('<', '>').ToLowerInvariant()
        : $"k:{h.Date.ToUnixTimeSeconds()}|{h.FromAddress.ToLowerInvariant()}|{h.Subject}";

    static string Glyph(string name) => name.ToLowerInvariant() switch
    {
        var n when n.Contains("inbox") || n.Contains("gelen") => "",
        var n when n.Contains("sent") || n.Contains("gönder") => "",
        var n when n.Contains("deleted") || n.Contains("silin") || n.Contains("trash") => "",
        var n when n.Contains("draft") || n.Contains("taslak") => "",
        _ => ""
    };
}

/// <summary>Reads .pst / .ost files with XstReader (MS-PL).</summary>
public sealed class XstArchiveSource : IMailArchiveSource
{
    readonly XstReader.XstFile _file;
    public XstArchiveSource(string path) { _file = new XstReader.XstFile(path); }

    public IEnumerable<ArchiveFolder> Folders()
    {
        var list = new List<ArchiveFolder>();
        void Walk(XstReader.XstFolder f, string path)
        {
            foreach (var c in f.Folders)
            {
                var p = path + "/" + c.DisplayName.Replace('/', '∕');
                var folder = c;
                list.Add(new ArchiveFolder(p, c.DisplayName, (int)c.ContentCount, () => Read(folder)));
                Walk(c, p);
            }
        }
        Walk(_file.RootFolder, "");
        return list;
    }

    static IEnumerable<ArchiveMessage> Read(XstReader.XstFolder folder)
    {
        foreach (var m in folder.Messages)
        {
            ArchiveMessage? result = null;
            try
            {
                string? html = null, text = null;
                var body = m.Body;
                if (body != null)
                {
                    if (body.Format == XstReader.XstMessageBodyFormat.Html) html = body.Text;
                    else if (body.Format == XstReader.XstMessageBodyFormat.PlainText) text = body.Text;
                    else text = body.Text;   // RTF: XstReader gives its text form
                }
                var sender = m.Recipients?.Sender ?? m.Recipients?.SentRepresenting;
                var messageId = m.Properties?.Get((ushort)0x1035)?.Value as string;   // PidTagInternetMessageId
                var atts = new List<ArchiveAttachment>(); var embedded = 0;
                foreach (var a in m.Attachments)
                {
                    if (a.IsEmail) { embedded++; continue; }
                    if (!a.IsFile || a.IsHidden) continue;
                    var att = a;
                    atts.Add(new ArchiveAttachment(string.IsNullOrWhiteSpace(a.LongFileName) ? a.FileName : a.LongFileName, a.Size, s => att.SaveToStream(s)));
                }
                result = new ArchiveMessage(m.Subject ?? "", sender?.DisplayName ?? m.From ?? "", sender?.Address ?? "", m.To ?? "", m.Cc ?? "",
                    m.ReceivedTime ?? m.SubmittedTime ?? m.Date, m.IsRead, html, text, messageId, atts, embedded);
            }
            catch (Exception) { result = null; }   // a damaged item is skipped, the rest of the archive still imports
            if (result != null) yield return result;
            m.ClearContents();
        }
    }

    public void Dispose() => _file.Dispose();
}

using System.Net;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MailStudio.Models;
using MimeKit;

namespace MailStudio.Services;

public record FolderInfo(string FullName, string DisplayName, string Icon, int Sort);

/// <summary>MailKit-based IMAP/SMTP layer. One IMAP connection per account, serialized access with a lock.</summary>
public sealed class MailService : IDisposable
{
    // Also initialize for consumers that use Core without the WPF/MCP entry points.
    static MailService() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    readonly MailCache _cache;
    readonly Dictionary<string, ImapClient> _clients = new();
    readonly Dictionary<string, SemaphoreSlim> _locks = new();

    readonly OAuthService _oauth;
    readonly LocalDraftStore _drafts;
    readonly SubmissionStore _submissions;

    public MailService(MailCache cache, OAuthService oauth, LocalDraftStore? drafts = null, SubmissionStore? submissions = null)
    { _cache = cache; _oauth = oauth; _drafts = drafts ?? new(); _submissions = submissions ?? new(); }

    /// <summary>Authenticates with a password or OAuth2 (XOAUTH2).</summary>
    static async Task AuthAsync(MailService_Auth target, AccountConfig a, OAuthService oauth, CancellationToken ct)
    {
        if (a.AuthType == AuthType.Password) await target.Authenticate(Cred(a), ct);
        else
        {
            var token = await oauth.GetAccessTokenAsync(a, ct);
            await target.AuthenticateSasl(new SaslMechanismOAuth2(a.Email, token), ct);
        }
    }

    SemaphoreSlim LockOf(string id)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(id, out var s)) _locks[id] = s = new SemaphoreSlim(1, 1);
            return s;
        }
    }

    static SecureSocketOptions Map(SecurityMode m) => m switch
    {
        SecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SecurityMode.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto
    };

    static NetworkCredential Cred(AccountConfig a) =>
        new(string.IsNullOrWhiteSpace(a.UserName) ? a.Email : a.UserName, AccountStore.Unprotect(a.ProtectedPassword));

    async Task<ImapClient> ClientAsync(AccountConfig a, CancellationToken ct, string key)
    {
        if (_clients.TryGetValue(key, out var c) && c.IsConnected && c.IsAuthenticated) return c;
        c?.Dispose();
        c = new ImapClient { Timeout = 60000 };
        await c.ConnectAsync(a.ImapHost, a.ImapPort, Map(a.ImapSecurity), ct);
        await AuthAsync(new MailService_Auth(c), a, _oauth, ct);
        _clients[key] = c;
        return c;
    }

    /// <summary>
    /// channel: "" = sync/operations connection, "read" = reading connection. Thanks to the separate connection
    /// a clicked mail opens without waiting while the background sync is running.
    /// </summary>
    async Task<T> WithClient<T>(AccountConfig a, Func<ImapClient, Task<T>> work, CancellationToken ct = default, string channel = "", bool retry = true)
    {
        var key = channel.Length == 0 ? a.Id : a.Id + ":" + channel;
        var l = LockOf(key);
        await l.WaitAsync(ct);
        try
        {
            try { return await work(await ClientAsync(a, ct, key)); }
            catch (Exception ex) when (retry && (ex is IOException or ServiceNotConnectedException or ImapProtocolException))
            {
                // If the connection dropped, retry once.
                if (_clients.Remove(key, out var dead)) dead.Dispose();
                return await work(await ClientAsync(a, ct, key));
            }
        }
        finally { l.Release(); }
    }

    public static async Task TestAsync(AccountConfig a, OAuthService oauth, CancellationToken ct = default)
    {
        using (var imap = new ImapClient { Timeout = 30000 })
        {
            await imap.ConnectAsync(a.ImapHost, a.ImapPort, Map(a.ImapSecurity), ct);
            await AuthAsync(new MailService_Auth(imap), a, oauth, ct);
            await imap.DisconnectAsync(true, ct);
        }
        using var smtp = new SmtpClient { Timeout = 30000 };
        await smtp.ConnectAsync(a.SmtpHost, a.SmtpPort, Map(a.SmtpSecurity), ct);
        await AuthAsync(new MailService_Auth(smtp), a, oauth, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    // ---------------- Folders ----------------

    public Task<List<FolderInfo>> GetFoldersAsync(AccountConfig a, CancellationToken ct = default) => DemoData.IsDemo(a) ? Task.FromResult(DemoData.Folders()) :
        WithClient(a, async c =>
        {
            var list = new List<FolderInfo> { new(c.Inbox.FullName, L.T("Gelen Kutusu"), "", 0) };
            var ns = c.PersonalNamespaces.Count > 0 ? c.PersonalNamespaces[0] : new FolderNamespace('/', "");
            foreach (var f in await c.GetFoldersAsync(ns, false, ct))
            {
                if (f.Attributes.HasFlag(FolderAttributes.NoSelect) || f.Attributes.HasFlag(FolderAttributes.NonExistent)) continue;
                if (string.Equals(f.FullName, c.Inbox.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(Describe(f));
            }
            return list.OrderBy(x => x.Sort).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, ct);

    static FolderInfo Describe(IMailFolder f)
    {
        var at = f.Attributes;
        var n = f.Name.ToLowerInvariant();
        if (at.HasFlag(FolderAttributes.Drafts) || n is "drafts" or "taslaklar") return new(f.FullName, L.T("Taslaklar"), "", 1);
        if (at.HasFlag(FolderAttributes.Sent) || n is "sent" or "sent items" or "sent mail" or "gönderilmiş öğeler" or "giden") return new(f.FullName, L.T("Gönderilmiş Öğeler"), "", 2);
        if (at.HasFlag(FolderAttributes.Trash) || n is "trash" or "deleted items" or "çöp kutusu" or "silinmiş öğeler") return new(f.FullName, L.T("Silinmiş Öğeler"), "", 3);
        if (at.HasFlag(FolderAttributes.Junk) || n is "spam" or "junk" or "junk e-mail" or "istenmeyen") return new(f.FullName, L.T("Gereksiz E-posta"), "", 4);
        if (at.HasFlag(FolderAttributes.Archive) || n is "archive" or "arşiv") return new(f.FullName, L.T("Arşiv"), "", 5);
        if (n is "newsletters" or "bültenler" or "bultenler") return new(f.FullName, L.T("Bültenler"), "\uE7BC", 7);
        if (at.HasFlag(FolderAttributes.Flagged)) return new(f.FullName, L.T("Yıldızlı"), "", 6);
        if (at.HasFlag(FolderAttributes.All)) return new(f.FullName, L.T("Tüm Postalar"), "", 60);
        return new(f.FullName, f.Name, "", 50);
    }

    public async Task<string?> FindSpecialAsync(AccountConfig a, int sort, CancellationToken ct = default)
    {
        var folders = await GetFoldersAsync(a, ct);
        return folders.FirstOrDefault(f => f.Sort == sort)?.FullName;
    }

    // ---------------- Senkron ----------------
    /// <summary>
    /// Like Outlook's "cached mode": pulls all headers of the last <see cref="AccountConfig.OfflineMonths"/> months
    /// into the local cache (adds new ones, refreshes flags, removes the ones deleted on the server).
    /// progress: (processed, total new) — on a big first sync the list fills up piece by piece.
    /// </summary>
    public Task<int> SyncFolderAsync(AccountConfig a, string folderName, CancellationToken ct = default, Action<int, int>? progress = null) => DemoData.IsDemo(a) ? Task.FromResult(0) :
        WithClient(a, async c =>
        {
            var folder = await c.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            _cache.SetUidValidity(a.Id, folder.FullName, folder.UidValidity);

            var query = a.OfflineMonths > 0 ? SearchQuery.DeliveredAfter(DateTime.Today.AddMonths(-a.OfflineMonths)) : SearchQuery.All;
            var window = await folder.SearchAsync(query, ct);
            var known = _cache.GetKnownUids(a.Id, folder.FullName);
            // Newest first so the list fills immediately
            var newUids = window.Where(u => !known.Contains(u.Id)).OrderByDescending(u => u.Id).ToList();

            int done = 0;
            if (newUids.Count > 0)
            {
                var req = NewMailFetch();
                foreach (var chunk in newUids.Chunk(100))
                {
                    var headers = new List<MailHeader>();
                    var pendingAtts = new List<(uint uid, List<AttachmentInfo> atts)>();
                    foreach (var s in await folder.FetchAsync(chunk, req, ct))
                    {
                        headers.Add(ToHeader(a.Id, folder.FullName, folder.UidValidity, s));
                        var atts = AttachmentsOf(s);
                        if (atts.Count > 0) pendingAtts.Add((s.UniqueId.Id, atts));
                    }
                    _cache.UpsertHeaders(headers);
                    foreach (var (uid, atts) in pendingAtts) _cache.SaveAttachments(a.Id, folder.FullName, uid, atts);
                    done += headers.Count;
                    progress?.Invoke(done, newUids.Count);
                }
            }

            // Refresh the read/flag state of known messages
            var knownInWindow = window.Where(u => known.Contains(u.Id)).ToList();
            foreach (var chunk in knownInWindow.Chunk(1000))
                foreach (var s in await folder.FetchAsync(chunk, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.References | MessageSummaryItems.Flags, ct))
                {
                    _cache.UpsertHeaders(new[] { ToHeader(a.Id, folder.FullName, folder.UidValidity, s) });
                    _cache.SetFlags(a.Id, folder.FullName, s.UniqueId.Id,
                        read: s.Flags?.HasFlag(MessageFlags.Seen) == true,
                        flagged: s.Flags?.HasFlag(MessageFlags.Flagged) == true);
                }

            // Remove the ones deleted on the server (old mails loaded manually outside the window are kept)
            var all = await folder.SearchAsync(SearchQuery.All, ct);
            _cache.RemoveMissing(a.Id, folder.FullName, all.Select(u => u.Id).ToHashSet());
            await folder.CloseAsync(false, ct);
            return done;
        }, ct);

    /// <summary>Fetches <paramref name="count"/> mails older than the oldest cached mail from the server ("Older messages").</summary>
    public Task<int> LoadOlderAsync(AccountConfig a, string folderName, DateTimeOffset before, int count = 200, CancellationToken ct = default) =>
        WithClient(a, async c =>
        {
            var folder = await c.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            var uids = await folder.SearchAsync(SearchQuery.DeliveredBefore(before.LocalDateTime.Date.AddDays(1)), ct);
            var known = _cache.GetKnownUids(a.Id, folder.FullName);
            var pick = uids.Where(u => !known.Contains(u.Id)).OrderByDescending(u => u.Id).Take(count).ToList();
            var headers = new List<MailHeader>();
            if (pick.Count > 0)
            {
                var req = NewMailFetch();
                foreach (var s in await folder.FetchAsync(pick, req, ct))
                {
                    headers.Add(ToHeader(a.Id, folder.FullName, folder.UidValidity, s));
                    var atts = AttachmentsOf(s);
                    _cache.UpsertHeaders(new[] { headers[^1] });
                    if (atts.Count > 0) _cache.SaveAttachments(a.Id, folder.FullName, s.UniqueId.Id, atts);
                }
            }
            await folder.CloseAsync(false, ct);
            return headers.Count;
        }, ct);

    /// <summary>Header fetch for new mail: also the few headers rules and newsletters need (no body is downloaded).</summary>
    static FetchRequest NewMailFetch() => new(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText | MessageSummaryItems.InternalDate | MessageSummaryItems.References | MessageSummaryItems.Size)
    { Headers = new HeaderSet(new[] { "List-Unsubscribe", "List-Unsubscribe-Post", "Importance", "X-Priority" }) };

    static string ImportanceOf(HeaderList? h)
    {
        var imp = (h?["Importance"] ?? "").Trim().ToLowerInvariant();
        if (imp is "high" or "low") return imp;
        var pri = (h?["X-Priority"] ?? "").Trim();
        return pri.StartsWith('1') || pri.StartsWith('2') ? "high" : pri.StartsWith('4') || pri.StartsWith('5') ? "low" : "normal";
    }

    static MailHeader ToHeader(string accountId, string folder, uint validity, IMessageSummary s)
    {
        var env = s.Envelope;
        var from = env?.From?.Mailboxes.FirstOrDefault();
        return new MailHeader
        {
            AccountId = accountId,
            Folder = folder,
            Uid = s.UniqueId.Id,
            UidValidity = validity,
            IsDraft = s.Flags?.HasFlag(MessageFlags.Draft) == true,
            ReplyTo = env?.ReplyTo?.ToString() ?? "",
            InReplyTo = env?.InReplyTo ?? "",
            References = s.References?.ToList() ?? new(),
            MessageId = env?.MessageId ?? "",
            FromName = FixTurkish(from?.Name),
            FromAddress = from?.Address ?? "",
            To = FixTurkish(env?.To?.ToString()),
            Subject = FixTurkish(env?.Subject),
            Preview = FixTurkish(CleanPreview(s.PreviewText)),
            Date = env?.Date ?? s.InternalDate ?? DateTimeOffset.Now,
            IsRead = s.Flags?.HasFlag(MessageFlags.Seen) == true,
            IsFlagged = s.Flags?.HasFlag(MessageFlags.Flagged) == true,
            HasAttachments = s.Attachments?.Any() == true,
            Size = (long)(s.Size ?? 0),
            Importance = ImportanceOf(s.Headers),
            ListUnsubscribe = (s.Headers?["List-Unsubscribe"] ?? "").Trim(),
            ListUnsubscribePost = (s.Headers?["List-Unsubscribe-Post"] ?? "").Trim()
        };
    }

    /// <summary>
    /// Keeps the Unicode result of the MIME decoder. In the old version this method replaced © signs by guessing;
    /// the correct fix is to register the legacy encoding provider before reading MIME.
    /// </summary>
    public static string FixTurkish(string? s)
        => s ?? "";

    /// <summary>Removes embedded image placeholders ([cid:...]) and signature lines from the preview.</summary>
    public static string CleanPreview(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        p = Regex.Replace(p, @"\[cid:[^\]]*\]", " ");
        p = Regex.Replace(p, @"\[image[^\]]*\]", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        p = Regex.Replace(p, @"[_\-=]{4,}", " ");
        p = Regex.Replace(p, @"\s+", " ");
        return p.Trim();
    }

    // ---------------- Message content ----------------

    static List<AttachmentInfo> AttachmentsOf(IMessageSummary s)
    {
        if (s.Body == null) return new();
        return s.Attachments.Select(p => new AttachmentInfo
        {
            FileName = p.FileName ?? p.ContentType?.Name ?? "ek",
            // Base64 encoded size ≈ real size × 4/3
            Size = string.Equals(p.ContentTransferEncoding, "base64", StringComparison.OrdinalIgnoreCase) ? (long)(p.Octets * 0.74) : (long)p.Octets,
            Part = p.PartSpecifier
        }).ToList();
    }

    /// <summary>
    /// Fast reading like Outlook: first only the text/HTML body and embedded images are downloaded,
    /// attachments are not (their list is shown; they are fetched with <see cref="DownloadAttachmentAsync"/> on click).
    /// </summary>
    public async Task<MailContent> GetContentAsync(AccountConfig a, MailHeader h, CancellationToken ct = default, bool markRead = true)
    {
        if (DemoData.IsDemo(a))
        {
            if (markRead) _cache.SetFlags(a.Id, h.Folder, h.Uid, read: true);
            return _cache.GetBody(a.Id, h.Folder, h.Uid) ?? new MailContent { Html = "<p></p>" };
        }
        var content = await WithClient(a, async c =>
        {
            var folder = await c.GetFolderAsync(h.Folder, ct);
            await folder.OpenAsync(markRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, ct);
            ValidateIdentity(a, h, folder);
            var uid = new UniqueId(h.Uid);
            var s = (await folder.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure | MessageSummaryItems.Envelope, ct)).FirstOrDefault()
                    ?? throw new InvalidOperationException(L.T("İleti sunucuda bulunamadı (silinmiş olabilir)."));

            string html = "", text = "";
            if (s.HtmlBody != null && await folder.GetBodyPartAsync(uid, s.HtmlBody, ct) is TextPart hp) html = hp.Text ?? "";
            if (s.TextBody != null && await folder.GetBodyPartAsync(uid, s.TextBody, ct) is TextPart tp) text = tp.Text ?? "";

            // Embedded (cid:) images
            if (html.Length > 0)
            {
                foreach (var part in s.BodyParts.OfType<BodyPartBasic>().Where(p => p.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)))
                {
                    var cid = part.ContentId?.Trim('<', '>');
                    if (!InlineImages.References(html,cid,part.ContentLocation?.ToString())) continue;
                    if (await folder.GetBodyPartAsync(uid, part, ct) is MimePart mp)
                    {
                        using var ms = new MemoryStream();
                        await (mp.Content ?? throw new InvalidDataException(L.T("Eksik MIME içeriği."))).DecodeToAsync(ms, ct);
                        html = InlineImages.Replace(html,cid,mp.ContentLocation?.ToString(),part.ContentType.MimeType,ms.ToArray());
                    }
                }
            }

            // Meeting invitation: text/calendar alternative (Outlook/Exchange/Google) or an attached .ics
            var ics = "";
            var calendarPart = s.BodyParts.OfType<BodyPartBasic>().FirstOrDefault(IsCalendarPart);
            if (calendarPart != null && calendarPart.Octets < 2_000_000 && await folder.GetBodyPartAsync(uid, calendarPart, ct) is MimePart cp)
                ics = CalendarText(cp);

            if (markRead && !h.IsRead) await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            await folder.CloseAsync(false, ct);

            if (html.Length == 0)
                html = "<pre style=\"font-family:Segoe UI,Calibri,sans-serif;white-space:pre-wrap;font-size:14px\">" + WebUtility.HtmlEncode(text) + "</pre>";
            if (text.Length == 0) text = HtmlToText(html);
            return new MailContent { Html = FixTurkish(html), Text = FixTurkish(text), Cc = FixTurkish(s.Envelope?.Cc?.ToString()), ReplyTo = s.Envelope?.ReplyTo?.ToString() ?? "", References = h.References, Attachments = AttachmentsOf(s), CalendarIcs = ics };
        }, ct, "read");

        _cache.SaveBody(a.Id, h.Folder, h.Uid, content);
        _cache.SaveAttachments(a.Id, h.Folder, h.Uid, content.Attachments);
        if (markRead) _cache.SetFlags(a.Id, h.Folder, h.Uid, read: true);
        return content;
    }

    /// <summary>Downloads a single attachment from the server and saves it to a folder.</summary>
    public Task<string> DownloadAttachmentAsync(AccountConfig a, MailHeader h, AttachmentInfo att, string targetDir, CancellationToken ct = default) => DemoData.IsDemo(a) ? DemoData.CopyAttachmentAsync(att, targetDir) :
        WithClient(a, async c =>
        {
            var folder = await c.GetFolderAsync(h.Folder, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            ValidateIdentity(a, h, folder);
            var uid = new UniqueId(h.Uid);
            var s = (await folder.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, ct)).First();
            var candidates = s.Attachments.Where(p => !string.IsNullOrEmpty(att.Part)
                ? p.PartSpecifier == att.Part : (p.FileName ?? p.ContentType?.Name) == att.FileName).ToList();
            if (candidates.Count != 1) throw new FileNotFoundException(L.T("Ek parçası bulunamadı veya adı birden çok eke ait: ") + att.FileName);
            var entity = await folder.GetBodyPartAsync(uid, candidates[0], ct);
            await folder.CloseAsync(false, ct);
            var path = AttachmentFiles.PathFor(targetDir, att);
            return await AttachmentFiles.WriteAsync(path, async stream =>
            {
                if (entity is MimePart mp) await (mp.Content ?? throw new InvalidDataException(L.T("Eksik MIME içeriği."))).DecodeToAsync(stream, ct);
                else if (entity is MessagePart msgp && msgp.Message != null) await msgp.Message.WriteToAsync(stream, ct);
                else throw new InvalidDataException(L.T("Desteklenmeyen ek içeriği."));
            }, ct);
        }, ct, "read");

    public async Task<(MailContent content, MimeMessage message)> GetMessageAsync(AccountConfig a, MailHeader h, CancellationToken ct = default, bool markRead = true)
    {
        if (DemoData.IsDemo(a))
        {
            var body = _cache.GetBody(a.Id, h.Folder, h.Uid) ?? new MailContent();
            var demo = new MimeMessage { Subject = h.Subject, Body = new TextPart("html") { Text = body.Html } };
            demo.From.Add(new MailboxAddress(h.FromName, h.FromAddress)); demo.To.Add(MailboxAddress.Parse(a.Email));
            if (!string.IsNullOrEmpty(body.ListUnsubscribe))
                demo.Headers.Add("List-Unsubscribe", body.ListUnsubscribe);
            if (!string.IsNullOrEmpty(body.ListUnsubscribePost))
                demo.Headers.Add("List-Unsubscribe-Post", body.ListUnsubscribePost);
            return (body, demo);
        }
        var msg = await WithClient(a, async c =>
        {
            var folder = await c.GetFolderAsync(h.Folder, ct);
            // ReadOnly open + BODY.PEEK: the read flag does not change
            await folder.OpenAsync(markRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, ct);
            ValidateIdentity(a, h, folder);
            var m = await folder.GetMessageAsync(new UniqueId(h.Uid), ct);
            if (markRead && !h.IsRead) await folder.AddFlagsAsync(new UniqueId(h.Uid), MessageFlags.Seen, true, ct);
            await folder.CloseAsync(false, ct);
            return m;
        }, ct);

        var content = BuildContent(msg);
        _cache.SaveBody(a.Id, h.Folder, h.Uid, content);
        if (markRead) _cache.SetFlags(a.Id, h.Folder, h.Uid, read: true);
        return (content, msg);
    }

    public static MailContent BuildContent(MimeMessage msg)
    {
        string html = msg.HtmlBody ?? "";
        string text = msg.TextBody ?? "";
        if (string.IsNullOrEmpty(html))
            html = "<pre style=\"font-family:Segoe UI,Calibri,sans-serif;white-space:pre-wrap;font-size:14px\">" +
                   WebUtility.HtmlEncode(text) + "</pre>";
        else
        {
            // turn cid: embedded images into data URIs
            foreach (var part in msg.BodyParts.OfType<MimePart>().Where(p => p.ContentType.MediaType.Equals("image",StringComparison.OrdinalIgnoreCase) && InlineImages.References(html,p.ContentId,p.ContentLocation?.ToString())))
            {
                using var ms = new MemoryStream();
                (part.Content ?? throw new InvalidDataException(L.T("Eksik MIME içeriği."))).DecodeTo(ms);
                html = InlineImages.Replace(html,part.ContentId,part.ContentLocation?.ToString(),part.ContentType.MimeType,ms.ToArray());
            }
        }
        if (string.IsNullOrEmpty(text)) text = HtmlToText(html);

        var atts = msg.Attachments.Select(p => new AttachmentInfo
        {
            FileName = (p as MimePart)?.FileName ?? (p as MessagePart)?.Message?.Subject ?? "ek",
            Size = p is MimePart mp && mp.Content != null ? EstimateSize(mp) : 0
        }).ToList();

        var calendar = msg.BodyParts.OfType<MimePart>().FirstOrDefault(p => p.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) || p.ContentType.MimeType.Equals("application/ics", StringComparison.OrdinalIgnoreCase) || (p.FileName ?? "").EndsWith(".ics", StringComparison.OrdinalIgnoreCase));
        var listUnsub = msg.Headers["List-Unsubscribe"] ?? "";
        var listUnsubPost = msg.Headers["List-Unsubscribe-Post"] ?? "";
        return new MailContent
        {
            Html = html,
            Text = text,
            Cc = msg.Cc.ToString(),
            ReplyTo = msg.ReplyTo.ToString(),
            References = msg.References.ToList(),
            Attachments = atts,
            CalendarIcs = calendar == null ? "" : CalendarText(calendar),
            ListUnsubscribe = listUnsub,
            ListUnsubscribePost = listUnsubPost
        };
    }

    static bool IsCalendarPart(BodyPartBasic p) =>
        p.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) || p.ContentType.MimeType.Equals("application/ics", StringComparison.OrdinalIgnoreCase) ||
        (p.FileName ?? "").EndsWith(".ics", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decoded iCalendar text of a MIME part (charset honoured, UTF-8 default).</summary>
    public static string CalendarText(MimePart part)
    {
        if (part is TextPart tp) return tp.Text ?? "";
        using var ms = new MemoryStream();
        part.Content?.DecodeTo(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    static long EstimateSize(MimePart p)
    {
        try { using var s = new MemoryStream(); (p.Content ?? throw new InvalidDataException(L.T("Eksik MIME içeriği."))).DecodeTo(s); return s.Length; } catch { return 0; }
    }

    public static string HtmlToText(string html)
    {
        var t = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        t = Regex.Replace(t, "<br\\s*/?>|</p>|</div>|</tr>|</li>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        t = Regex.Replace(t, "<[^>]+>", "");
        t = WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, "[ \\t]+", " ");
        t = Regex.Replace(t, "\\n\\s*\\n+", "\n\n");
        return t.Trim();
    }

    public async Task<string> SaveAttachmentAsync(MimeMessage msg, string fileName, string targetDir)
    {
        var part = msg.Attachments.OfType<MimePart>().FirstOrDefault(p => p.FileName == fileName)
                   ?? throw new FileNotFoundException(fileName);
        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, string.Join("_", fileName.Split(Path.GetInvalidFileNameChars())));
        return await AttachmentFiles.WriteAsync(path, stream => (part.Content ?? throw new InvalidDataException(L.T("Eksik MIME içeriği."))).DecodeToAsync(stream));
    }

    // ---------------- Flags / moving ----------------

    public Task SetReadAsync(AccountConfig a, MailHeader h, bool read) => DemoData.IsDemo(a) ? Task.Run(() => _cache.SetFlags(a.Id, h.Folder, h.Uid, read: read)) :
        WithClient(a, async c =>
        {
            var f = await c.GetFolderAsync(h.Folder);
            await f.OpenAsync(FolderAccess.ReadWrite);
            ValidateIdentity(a, h, f);
            if (read) await f.AddFlagsAsync(new UniqueId(h.Uid), MessageFlags.Seen, true);
            else await f.RemoveFlagsAsync(new UniqueId(h.Uid), MessageFlags.Seen, true);
            await f.CloseAsync(false);
            _cache.SetFlags(a.Id, h.Folder, h.Uid, read: read);
            return true;
        }, retry: false);

    public Task SetFlaggedAsync(AccountConfig a, MailHeader h, bool flagged) => DemoData.IsDemo(a) ? Task.Run(() => _cache.SetFlags(a.Id, h.Folder, h.Uid, flagged: flagged)) :
        WithClient(a, async c =>
        {
            var f = await c.GetFolderAsync(h.Folder);
            await f.OpenAsync(FolderAccess.ReadWrite);
            ValidateIdentity(a, h, f);
            if (flagged) await f.AddFlagsAsync(new UniqueId(h.Uid), MessageFlags.Flagged, true);
            else await f.RemoveFlagsAsync(new UniqueId(h.Uid), MessageFlags.Flagged, true);
            await f.CloseAsync(false);
            _cache.SetFlags(a.Id, h.Folder, h.Uid, flagged: flagged);
            return true;
        }, retry: false);

    static void ValidateIdentity(AccountConfig account, MailHeader h, IMailFolder folder)
    {
        if (account.Id != h.AccountId || h.Uid == 0 || h.UidValidity == 0 || h.UidValidity != folder.UidValidity)
            throw new InvalidOperationException(L.T("İletinin klasör kimliği değişmiş veya doğrulanamıyor. Klasörü yenileyin."));
    }

    /// <summary>A normal delete only moves to the trash folder; a missing target never turns into a permanent delete.</summary>
    public async Task MoveAsync(AccountConfig a, MailHeader h, string? target)
    {
        if (DemoData.IsDemo(a))
        {
            if (target != null) _cache.MoveMessage(a.Id, h.Folder, target, h.Uid);
            else _cache.Delete(a.Id, h.Folder, h.Uid);
            return;
        }
        target ??= await FindSpecialAsync(a, 3);
        if (target == null) throw new InvalidOperationException(L.T("Silinmiş Öğeler klasörü bulunamadı; ileti silinmedi."));
        if (string.Equals(target, h.Folder, StringComparison.Ordinal))
            throw new InvalidOperationException(L.T("İleti zaten hedef klasörde. Kalıcı silme için ayrı komutu kullanın."));
        await WithClient(a, async c =>
        {
            var f = await c.GetFolderAsync(h.Folder);
            await f.OpenAsync(FolderAccess.ReadWrite);
            ValidateIdentity(a, h, f);
            if (!c.Capabilities.HasFlag(ImapCapabilities.Move) && !c.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                throw new NotSupportedException(L.T("Sunucu diğer silinmiş iletileri etkilemeden güvenli taşıma desteklemiyor (MOVE/UIDPLUS)."));
            await f.MoveToAsync(new UniqueId(h.Uid), await c.GetFolderAsync(target));
            await f.CloseAsync(false);
            return true;
        }, retry: false);
        _cache.Delete(a.Id, h.Folder, h.Uid);
    }

    /// <summary>Returns the full name of a top-level folder with this name (case-insensitive), creating it if it does not exist.</summary>
    public async Task<string> EnsureFolderAsync(AccountConfig a, string name, CancellationToken ct = default)
    {
        if (DemoData.IsDemo(a)) return name;
        return await WithClient(a, async c =>
        {
            var root = c.PersonalNamespaces.Count > 0 ? c.GetFolder(c.PersonalNamespaces[0]) : c.Inbox.ParentFolder ?? c.Inbox;
            foreach (var f in await root.GetSubfoldersAsync(false, ct))
                if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f.FullName;
            var created = await root.CreateAsync(name, true, ct);
            return created.FullName;
        }, ct);
    }

    public async Task DeletePermanentlyAsync(AccountConfig a, MailHeader h)
    {
        if (DemoData.IsDemo(a)) { _cache.Delete(a.Id, h.Folder, h.Uid); return; }
        await WithClient(a, async c =>
        {
            if (!c.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                throw new NotSupportedException(L.T("Güvenli kalıcı silme için sunucuda UIDPLUS desteği gerekli."));
            var f = await c.GetFolderAsync(h.Folder);
            await f.OpenAsync(FolderAccess.ReadWrite);
            ValidateIdentity(a, h, f);
            var uid = new UniqueId(h.Uid);
            await f.AddFlagsAsync(uid, MessageFlags.Deleted, true);
            await f.ExpungeAsync(new[] { uid });
            await f.CloseAsync(false);
            return true;
        }, retry: false);
        _cache.Delete(a.Id, h.Folder, h.Uid);
    }

    // ---------------- Sending ----------------

    /// <summary>The local recovery copy is saved first, the server draft second. APPEND is never repeated automatically.</summary>
    public async Task<string> SaveDraftAsync(AccountConfig a, ComposeRequest r, CancellationToken ct = default)
    {
        _drafts.Save(a.Id, r);
        if (DemoData.IsDemo(a)) return "Drafts";
        using var msg = BuildMessage(a, r);
        msg.Headers.Add("X-MailStudio-Draft-Id", r.DraftId);
        if (r.SubmissionId != null) msg.Headers.Add("X-MailStudio-Submission-Id", r.SubmissionId);
        if (r.SubmissionState != null) msg.Headers.Add("X-MailStudio-Submission-State", r.SubmissionState);
        var drafts = r.DraftAccountId == a.Id && r.DraftFolder != null ? r.DraftFolder
            : await FindSpecialAsync(a, 1, ct) ?? throw new InvalidOperationException(L.T("Taslak yerel olarak korundu; sunucuda Taslaklar klasörü bulunamadı."));
        await WithClient(a, async c =>
        {
            var f = await c.GetFolderAsync(drafts, ct);
            await f.OpenAsync(FolderAccess.ReadWrite, ct);
            var old = (await f.SearchAsync(SearchQuery.HeaderContains("X-MailStudio-Draft-Id", r.DraftId), ct)).ToList();
            if (r.DraftAccountId == a.Id && r.DraftFolder == drafts && r.DraftUid.HasValue)
            {
                if (r.DraftUidValidity != f.UidValidity)
                    throw new InvalidOperationException(L.T("Taslak klasörünün kimliği değişti. Yerel kopya korundu; klasörü yenileyin."));
                old.Add(new UniqueId(r.DraftUid.Value));
            }
            old = old.Distinct().ToList();
            if (old.Count > 0 && !c.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                throw new NotSupportedException(L.T("Yerel taslak kaydedildi. Sunucu taslağını güvenle değiştirmek için UIDPLUS desteği gerekli."));
            var uid = await f.AppendAsync(msg, MessageFlags.Draft | MessageFlags.Seen, ct);
            // Record the new identity BEFORE removing the previous copy: either revision can be recovered after a crash.
            r.DraftAccountId = a.Id; r.DraftFolder = drafts; r.DraftUid = uid?.Id; r.DraftUidValidity = f.UidValidity;
            _drafts.Save(a.Id, r);
            if (old.Count > 0)
            {
                await f.AddFlagsAsync(old, MessageFlags.Deleted, true, ct);
                await f.ExpungeAsync(old, ct);
                foreach (var previous in old) _cache.Delete(a.Id, drafts, previous.Id);
            }
            await f.CloseAsync(false, ct);
            return true;
        }, ct, retry: false);
        return drafts;
    }

    public async Task<ComposeRequest> OpenDraftAsync(AccountConfig a, MailHeader h, string attachmentDir, CancellationToken ct = default)
    {
        var (body, msg) = await GetMessageAsync(a, h, ct, markRead: false);
        using (msg)
        {
            var id = msg.Headers["X-MailStudio-Draft-Id"];
            var request = new ComposeRequest
            {
                DraftId = Guid.TryParseExact(id, "N", out var guid) ? guid.ToString("N") : Guid.NewGuid().ToString("N"),
                DraftAccountId = a.Id, DraftFolder = h.Folder, DraftUid = h.Uid, DraftUidValidity = h.UidValidity,
                To = msg.To.ToString(), Cc = msg.Cc.ToString(), Bcc = msg.Bcc.ToString(), Subject = msg.Subject ?? "",
                Body = body.Text, BodyHtml = body.Html,
                SubmissionId = msg.Headers["X-MailStudio-Submission-Id"], SubmissionState = msg.Headers["X-MailStudio-Submission-State"], InReplyTo = msg.InReplyTo, References = msg.References.ToList()
            };
            if(Guid.TryParseExact(id,"N",out _))
            {
                var document=new AngleSharp.Html.Parser.HtmlParser().ParseDocument(body.Html);
                if(document.QuerySelector(".ms-draft-section") is {} editable)
                {
                    request.EditableHtml=editable.InnerHtml;
                    request.SignatureHtml=document.QuerySelector(".ms-signature-section")?.InnerHtml??"";
                    request.QuotedHtml=document.QuerySelector(".ms-quote-section")?.InnerHtml??"";
                }
            }
            // The complete MIME message is already available. Preserve every attachment by its MIME order,
            // including duplicate names, without a second network fetch or a filename-based lookup.
            var index = 0;
            foreach (var entity in msg.Attachments)
            {
                var att = new AttachmentInfo
                {
                    FileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? "ek.eml",
                    Part = "draft-attachment-" + (++index)
                };
                request.AttachmentPaths.Add(await AttachmentFiles.WriteAsync(AttachmentFiles.PathFor(attachmentDir, att), async stream =>
                {
                    if (entity is MimePart part) await (part.Content ?? throw new InvalidDataException(L.T("Eksik MIME içeriği."))).DecodeToAsync(stream, ct);
                    else if (entity is MessagePart message && message.Message != null) await message.Message.WriteToAsync(stream, ct);
                    else throw new InvalidDataException(L.T("Taslağın ek içeriği okunamadı."));
                }, ct));
            }
            return request;
        }
    }

    public async Task<SendResult> SendAsync(AccountConfig a, ComposeRequest r, CancellationToken ct = default)
    {
        if (DemoData.IsDemo(a)) throw new InvalidOperationException(L.T("Demo modunda gönderim kapalıdır; hiçbir ileti gönderilmez."));
        if (r.SubmissionState is "Sending" or "Uncertain" or "Accepted" or "Completed")
            throw new InvalidOperationException(L.T("Bu taslağın önceki gönderimi tekrar başlatılamaz. Gönderim kaydını kontrol edin: ") + r.SubmissionId);
        using var msg = BuildMessage(a, r);
        var id = await _submissions.PrepareAsync(a.Id, msg, ct);
        r.SubmissionId = id; r.SubmissionState = "Prepared";
        var warnings = new List<string>();
        using (var smtp = new SmtpClient { Timeout = 60000 })
        {
            try
            {
                await smtp.ConnectAsync(a.SmtpHost, a.SmtpPort, Map(a.SmtpSecurity), ct);
                await AuthAsync(new MailService_Auth(smtp), a, _oauth, ct);
            }
            catch (Exception ex) { r.SubmissionState = "FailedBeforeSubmission"; _submissions.Set(id, a.Id, msg.MessageId!, "FailedBeforeSubmission", ex.Message); throw; }
            _submissions.Set(id, a.Id, msg.MessageId!, "Sending");
            r.SubmissionState = "Sending";
            _drafts.Save(a.Id, r); // Recovery must remember that the server may accept this attempt.
            try { await smtp.SendAsync(msg, ct); }
            catch (Exception ex)
            {
                r.SubmissionState = "Uncertain";
                string? recoveryError = null;
                try { _drafts.Save(a.Id, r); } catch (Exception saveError) { recoveryError = saveError.Message; }
                try { _submissions.Set(id, a.Id, msg.MessageId!, "Uncertain", ex.Message); } catch { }
                throw new SubmissionUncertainException(L.T("Gönderim sonucu doğrulanamadı. Tekrar göndermeden Gönderilmiş Öğeler'i ve sunucuyu kontrol edin. Yerel gönderim kaydı: ") + id + (recoveryError == null ? "" : L.T("\nKurtarma kaydı güncellenemedi: ") + recoveryError), ex);
            }
            // SMTP has accepted the message. All subsequent failures are warnings, never a send retry.
            r.SubmissionState = "Accepted";
            try { _drafts.Save(a.Id, r); } catch (Exception ex) { warnings.Add(L.T("Yerel taslak durumu güncellenemedi: ") + ex.Message); }
            try { _submissions.Set(id, a.Id, msg.MessageId!, "Accepted"); }
            catch (Exception ex) { warnings.Add(L.T("Kabul kaydı güncellenemedi: ") + ex.Message); }
            try { await smtp.DisconnectAsync(true, CancellationToken.None); }
            catch (Exception ex) { warnings.Add(L.T("Sunucu iletiyi kabul etti; bağlantı kapanışı tamamlanamadı: ") + ex.Message); }
        }
        var copyWarning = await AfterSendAsync(a, msg, CancellationToken.None);
        if (copyWarning != null) warnings.Add(copyWarning);
        try
        {
            await RemoveSentDraftAsync(a, r);
            _drafts.Delete(r.DraftId);
        }
        catch (Exception ex) { warnings.Add(L.T("Gönderim başarılı; taslak temizlenemedi: ") + ex.Message); }
        var warning = warnings.Count == 0 ? null : string.Join("\n", warnings);
        try { _submissions.Set(id, a.Id, msg.MessageId!, copyWarning == null ? "Completed" : "SentCopyPending", warning); }
        catch (Exception ex) { warning = (warning == null ? "" : warning + "\n") + L.T("Yerel durum kaydedilemedi: ") + ex.Message; }
        r.SubmissionState = "Completed";
        return new SendResult(id, warning, msg.MessageId);
    }

    async Task RemoveSentDraftAsync(AccountConfig a, ComposeRequest r)
    {
        if (r.DraftAccountId != a.Id || r.DraftFolder == null) return;
        await WithClient(a, async c =>
        {
            if (!c.Capabilities.HasFlag(ImapCapabilities.UidPlus)) throw new NotSupportedException(L.T("UIDPLUS gerekli."));
            var f = await c.GetFolderAsync(r.DraftFolder);
            await f.OpenAsync(FolderAccess.ReadWrite);
            if (r.DraftUidValidity != f.UidValidity) throw new InvalidOperationException(L.T("Taslak klasörü değişmiş."));
            var uids = (await f.SearchAsync(SearchQuery.HeaderContains("X-MailStudio-Draft-Id", r.DraftId))).ToList();
            if (r.DraftUid.HasValue) uids.Add(new UniqueId(r.DraftUid.Value));
            uids = uids.Distinct().ToList();
            if (uids.Count > 0)
            {
                await f.AddFlagsAsync(uids, MessageFlags.Deleted, true);
                await f.ExpungeAsync(uids);
                foreach (var uid in uids) _cache.Delete(a.Id, r.DraftFolder, uid.Id);
            }
            await f.CloseAsync(false);
            return true;
        }, retry: false);
    }

    static MimeMessage BuildMessage(AccountConfig a, ComposeRequest r)
    {
        var msg = new MimeMessage { MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(), Date = DateTimeOffset.Now };
        msg.From.Add(new MailboxAddress(a.DisplayName, a.Email));
        if (!string.IsNullOrWhiteSpace(r.To)) msg.To.AddRange(InternetAddressList.Parse(r.To.Replace(';', ',')));
        if (!string.IsNullOrWhiteSpace(r.Cc)) msg.Cc.AddRange(InternetAddressList.Parse(r.Cc.Replace(';', ',')));
        if (!string.IsNullOrWhiteSpace(r.Bcc)) msg.Bcc.AddRange(InternetAddressList.Parse(r.Bcc.Replace(';', ',')));
        msg.Subject = r.Subject;
        foreach (var reference in r.References.Distinct(StringComparer.Ordinal)) msg.References.Add(reference);
        if (!string.IsNullOrEmpty(r.InReplyTo))
        {
            msg.InReplyTo = r.InReplyTo;
            if (!msg.References.Contains(r.InReplyTo)) msg.References.Add(r.InReplyTo);
        }

        var html = !string.IsNullOrEmpty(r.BodyHtml)
            ? r.BodyHtml
            : "<div style=\"font-family:Calibri,Segoe UI,sans-serif;font-size:11pt\">" +
              WebUtility.HtmlEncode(r.Body).Replace("\r\n", "\n").Replace("\n", "<br>") + "</div>";
        var bb = new BodyBuilder { TextBody = string.IsNullOrEmpty(r.Body) ? HtmlToText(html) : r.Body };

        // Images pasted/inserted into the editor (data: URI) → embedded CID images (Outlook/Gmail show them that way)
        html = Regex.Replace(html, "src=[\"']data:(?<mime>image/[a-zA-Z0-9.+-]+);base64,(?<data>[^\"']+)[\"']", m =>
        {
            try
            {
                var bytes = Convert.FromBase64String(m.Groups["data"].Value);
                var ext = m.Groups["mime"].Value.Split('/')[1].Replace("jpeg", "jpg");
                var res = bb.LinkedResources.Add($"resim{bb.LinkedResources.Count + 1}.{ext}", bytes, ContentType.Parse(m.Groups["mime"].Value));
                res.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId();
                return $"src=\"cid:{res.ContentId}\"";
            }
            catch { return m.Value; }
        });
        bb.HtmlBody = "<html><head><meta charset=\"utf-8\"></head><body>" + html + "</body></html>";
        foreach (var p in r.AttachmentPaths) bb.Attachments.Add(p);
        msg.Body = string.IsNullOrWhiteSpace(r.CalendarIcs) ? bb.ToMessageBody() : WithCalendar(bb.ToMessageBody(), r.CalendarIcs, r.CalendarMethod ?? "REQUEST");
        return msg;
    }

    /// <summary>Adds a meeting the way Outlook does: a text/calendar alternative (Outlook, Gmail and phones show
    /// Accept/Decline for it) and the same data as an invite.ics attachment for other mail programs.</summary>
    public static MimeEntity WithCalendar(MimeEntity body, string ics, string method)
    {
        var calendar = new TextPart("calendar") { Text = ics, ContentTransferEncoding = ContentEncoding.Base64 };
        calendar.ContentType.Charset = "utf-8";
        calendar.ContentType.Parameters.Add("method", method.ToUpperInvariant());
        var file = new MimePart("application", "ics")
        {
            Content = new MimeContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ics))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = method.Equals("CANCEL", StringComparison.OrdinalIgnoreCase) ? "cancel.ics" : method.Equals("REPLY", StringComparison.OrdinalIgnoreCase) ? "reply.ics" : "invite.ics"
        };
        Multipart? alternative = body as Multipart is { ContentType.MediaSubtype: var st } m && st.Equals("alternative", StringComparison.OrdinalIgnoreCase) ? m
            : (body as Multipart)?.OfType<Multipart>().FirstOrDefault(x => x.ContentType.MediaSubtype.Equals("alternative", StringComparison.OrdinalIgnoreCase));
        if (alternative == null)
        {
            alternative = new Multipart("alternative");
            if (body is Multipart mixed && mixed.ContentType.MediaSubtype.Equals("mixed", StringComparison.OrdinalIgnoreCase) && mixed.Count > 0)
            {
                var first = mixed[0]; mixed.RemoveAt(0); alternative.Add(first); mixed.Insert(0, alternative);
            }
            else { alternative.Add(body); body = alternative; }
        }
        alternative.Add(calendar);
        if (body is Multipart top && top.ContentType.MediaSubtype.Equals("mixed", StringComparison.OrdinalIgnoreCase)) { top.Add(file); return top; }
        return new Multipart("mixed") { body, file };
    }

    async Task<string?> AfterSendAsync(AccountConfig a, MimeMessage msg, CancellationToken ct)
    {
        // Gmail stores sent mail itself; for the others append to Sent Items.
        if (!a.SmtpHost.Contains("gmail", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var sent = await FindSpecialAsync(a, 2, ct);
                if (sent == null) return L.T("Gönderim başarılı; Gönderilmiş Öğeler klasörü bulunamadı. Yerel .eml kopyası korundu.");
                await WithClient(a, async c =>
                    {
                        var f = await c.GetFolderAsync(sent, ct);
                        await f.OpenAsync(FolderAccess.ReadWrite, ct);
                        await f.AppendAsync(msg, MessageFlags.Seen, ct);
                        await f.CloseAsync(false, ct);
                        return true;
                    }, ct, retry: false);
            }
            catch (Exception ex) { return L.T("Gönderim başarılı; Gönderilmiş Öğeler kopyası kaydedilemedi. Yerel .eml kopyası korundu: ") + ex.Message; }
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) { try { c.Disconnect(true); } catch { } c.Dispose(); }
        _clients.Clear();
    }
}

/// <summary>Common authentication wrapper for the IMAP and SMTP clients.</summary>
readonly struct MailService_Auth
{
    readonly MailKit.IMailService _svc;
    public MailService_Auth(MailKit.IMailService svc) => _svc = svc;
    public Task Authenticate(System.Net.ICredentials c, CancellationToken ct) => _svc.AuthenticateAsync(c, ct);
    public Task AuthenticateSasl(SaslMechanism m, CancellationToken ct) => _svc.AuthenticateAsync(m, ct);
}

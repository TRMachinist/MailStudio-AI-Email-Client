using System.Text.RegularExpressions;
using MailStudio.Models;
using MailStudio.Services.Calendar;

namespace MailStudio.Services;

/// <summary>One file of a part found in a mail (drawing, model, NC program, archive...).</summary>
public sealed record PartFile(MailHeader Mail, AttachmentInfo Attachment, string Revision, string Kind)
{
    public string FileName => Attachment.FileName;
    public DateTimeOffset Date => Mail.Date;
}

/// <summary>A revision of the part with the files that came with it, oldest first.</summary>
public sealed record PartRevision(string Revision, DateTimeOffset FirstSeen, IReadOnlyList<PartFile> Files);

/// <summary>Everything the mailbox and the stores know about one part number.</summary>
public sealed class PartHistory
{
    public string PartNumber { get; init; } = "";
    public List<MailHeader> Mails { get; } = new();
    public List<PartFile> Files { get; } = new();
    public List<PartRevision> Revisions { get; } = new();
    public List<QuoteItem> Quotes { get; } = new();
    public List<TaskItem> Tasks { get; } = new();
    public List<string> Customers { get; } = new();
    public string LatestRevision => Revisions.Count > 0 ? Revisions[^1].Revision : "";
    public DateTimeOffset? FirstSeen => Mails.Count > 0 ? Mails.Min(m => m.Date) : null;
    public DateTimeOffset? LastSeen => Mails.Count > 0 ? Mails.Max(m => m.Date) : null;
    public IEnumerable<PartFile> NcPrograms => Files.Where(f => f.Kind == "nc");

    static readonly string[] NcExt = { ".nc", ".tap", ".mpf", ".spf", ".h", ".eia", ".gcode", ".ngc", ".cnc", ".min", ".ptp", ".iso", ".001" };

    public static string KindOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" or ".tif" or ".tiff" => "drawing",
        ".dxf" or ".dwg" or ".dwf" => "cad2d",
        ".step" or ".stp" or ".igs" or ".iges" or ".x_t" or ".sldprt" or ".catpart" or ".prt" or ".jt" or ".stl" => "model",
        ".xlsx" or ".xlsm" or ".xls" or ".csv" => "sheet",
        ".zip" or ".rar" or ".7z" => "archive",
        var e when NcExt.Contains(e) => "nc",
        _ => "other"
    };

    /// <summary>Regex for a part number that tolerates separators and case ("NW-1042" = "nw_1042" = "NW 1042"),
    /// but not a longer number ("NW-10421").</summary>
    public static Regex Pattern(string part)
    {
        var tokens = Regex.Matches(part.ToUpperInvariant(), "[A-Z]+|[0-9]+").Select(m => Regex.Escape(m.Value)).ToList();
        if (tokens.Count == 0) throw new ArgumentException(L.T("Parça numarası boş."));
        return new Regex(@"(?<![A-Za-z0-9])" + string.Join(@"[\s\-_./]?", tokens) + @"(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static PartHistory Build(string partNumber, MailCache cache, QuoteStore? quotes = null, TaskStore? tasks = null, int limit = 500)
    {
        var part = partNumber.Trim();
        var rx = Pattern(part);
        var tokens = Regex.Matches(part.ToUpperInvariant(), "[A-Z]+|[0-9]+").Select(m => m.Value).ToList();
        var like = "%" + string.Join("%", tokens) + "%";
        var h = new PartHistory { PartNumber = part };

        var seen = new HashSet<(string, string, uint)>();
        var candidates = cache.FindLike(like, limit).Concat(SafeSearch(cache, part, limit));
        foreach (var m in candidates)
        {
            if (!seen.Add((m.AccountId, m.Folder, m.Uid))) continue;
            var atts = m.HasAttachments ? cache.GetAttachments(m.AccountId, m.Folder, m.Uid) : new List<AttachmentInfo>();
            var body = rx.IsMatch(m.Subject) || rx.IsMatch(m.Preview) || atts.Any(a => rx.IsMatch(a.FileName));
            if (!body)
            {
                var content = cache.GetBody(m.AccountId, m.Folder, m.Uid);
                body = content != null && (rx.IsMatch(content.Text ?? "") || rx.IsMatch(content.Html ?? ""));
            }
            if (!body) continue;
            h.Mails.Add(m);
            var (_, mailRev) = RevisionService.ParsePartAndRevision(m.Subject + " " + m.Preview);
            foreach (var a in atts)
            {
                var (_, fileRev) = RevisionService.ParsePartAndRevision("", a.FileName);
                // files of a mail about the part belong to it; in a mail about several parts only files naming it do
                h.Files.Add(new PartFile(m, a, fileRev.Length > 0 ? fileRev : mailRev, KindOf(a.FileName)));
            }
        }
        h.Mails.Sort((a, b) => a.Date.CompareTo(b.Date));
        h.Files.Sort((a, b) => a.Date.CompareTo(b.Date));
        foreach (var g in h.Files.Where(f => f.Revision.Length > 0).GroupBy(f => f.Revision.ToUpperInvariant()))
            h.Revisions.Add(new PartRevision(g.First().Revision, g.Min(f => f.Date), g.ToList()));
        h.Revisions.Sort((a, b) => a.FirstSeen != b.FirstSeen ? a.FirstSeen.CompareTo(b.FirstSeen) : string.Compare(a.Revision, b.Revision, StringComparison.OrdinalIgnoreCase));

        var key = RevisionService.PartKey(part);
        if (quotes != null) h.Quotes.AddRange(quotes.Quotes.Where(q => RevisionService.PartKey(q.PartNumber) == key || rx.IsMatch(q.PartNumber)).OrderBy(q => q.CreatedAt));
        if (tasks != null) h.Tasks.AddRange(tasks.Tasks.Where(t => rx.IsMatch(t.Title) || rx.IsMatch(t.Notes)));
        h.Customers.AddRange(h.Mails.Where(m => !string.IsNullOrWhiteSpace(m.FromName) || !string.IsNullOrWhiteSpace(m.FromAddress))
            .GroupBy(m => m.FromAddress.ToLowerInvariant()).OrderByDescending(g => g.Count())
            .Select(g => string.IsNullOrWhiteSpace(g.First().FromName) ? g.Key : g.First().FromName).Take(6));
        return h;
    }

    static IEnumerable<MailHeader> SafeSearch(MailCache cache, string part, int limit)
    {
        try { return cache.Search(null, part, limit); }
        catch (Exception) { return Array.Empty<MailHeader>(); }   // FTS syntax edge cases: the LIKE query already covers it
    }

    /// <summary>Part numbers mentioned in a mail (subject first, then attachment names) for the "part card" command.</summary>
    public static List<string> PartsIn(MailHeader m, IEnumerable<AttachmentInfo> atts)
    {
        var list = new List<string>();
        void Add(string p) { p = p.Trim(' ', '-', '_', '.'); if (p.Length >= 3 && p.Any(char.IsDigit) && !list.Any(x => RevisionService.PartKey(x) == RevisionService.PartKey(p))) list.Add(p); }
        var (sp, _) = RevisionService.ParsePartAndRevision(m.Subject);
        if (sp.Length > 0) Add(sp);
        foreach (var a in atts)
        {
            var (fp, _) = RevisionService.ParsePartAndRevision("", a.FileName);
            if (fp.Length > 0) Add(fp);
        }
        return list;
    }
}

using System.Text;
using System.Text.RegularExpressions;
using MailStudio.Models;
using MailStudio.Services.Calendar;

namespace MailStudio.Services;

/// <summary>One line of the morning brief, pointing at the mail / task / event / quote it is about.</summary>
public sealed record BriefItem(string Title, string Detail, MailHeader? Mail = null, TaskItem? Task = null, QuoteItem? Quote = null, CalendarEvent? Event = null, bool Urgent = false);

/// <summary>A titled group of brief lines.</summary>
public sealed record BriefSection(string Key, string Title, List<BriefItem> Items);

/// <summary>
/// The day at a glance, built only from local data (no AI, nothing leaves the computer): today's appointments,
/// overdue and due tasks, quote deadlines and quotes waiting for an answer, unanswered sent mails, and the new mail
/// since the last working day sorted into requests for quote, revisions, orders / delivery and the rest.
/// </summary>
public sealed class MorningBrief
{
    public DateTime Day { get; init; }
    public DateTimeOffset Since { get; init; }
    public List<BriefSection> Sections { get; } = new();
    public int NewMails { get; set; }
    public int UnreadMails { get; set; }
    public BriefSection? this[string key] => Sections.FirstOrDefault(s => s.Key == key);
    public bool IsEmpty => Sections.All(s => s.Items.Count == 0);

    static readonly Regex Revision = new(@"\b(rev(ision|izyon)?\.?\s*[A-Z0-9]{1,2}\b|revizyon|revision|güncel(lendi|leme)|updated drawing|ECN|ECO|değişiklik)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Order = new(@"\b(sipariş|order|PO\s?#?\d|purchase order|teslim(at)?|delivery|sevk|shipment|ship|irsaliye|fatura|invoice|termin)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Start of "new mail" for a day: the previous working day's evening (Monday looks back to Friday 17:00).</summary>
    public static DateTimeOffset DefaultSince(DateTime day)
    {
        var prev = day.Date.AddDays(-1);
        while (prev.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) prev = prev.AddDays(-1);
        return new DateTimeOffset(prev.AddHours(17));
    }

    public static MorningBrief Build(DateTime day, MailCache cache, CalendarStore? calendar, TaskStore? tasks, QuoteStore? quotes, FollowUpStore? followUps, DateTimeOffset? since = null, int quoteWaitDays = 5)
    {
        var b = new MorningBrief { Day = day.Date, Since = since ?? DefaultSince(day) };
        var dayEnd = day.Date.AddDays(1);

        // ---- calendar
        var events = new List<BriefItem>();
        if (calendar != null)
            foreach (var o in calendar.Occurrences(day.Date, dayEnd, includeCancelled: false).OrderBy(o => o.Start))
                events.Add(new BriefItem(o.Event.Title.Length > 0 ? o.Event.Title : L.T("(başlıksız)"),
                    (o.Event.AllDay ? L.T("Tüm gün") : $"{o.Start:HH:mm}–{o.End:HH:mm}") + (o.Event.Location.Length > 0 ? " · " + o.Event.Location : ""), Event: o.Event));
        b.Sections.Add(new BriefSection("calendar", L.T("Bugünün takvimi"), events));

        // ---- tasks
        var due = new List<BriefItem>();
        if (tasks != null)
            foreach (var t in tasks.Tasks.Where(t => !t.Completed && t.DueDate is { } d && d.Date <= day.Date).OrderBy(t => t.DueDate))
            {
                var late = t.DueDate!.Value.Date < day.Date;
                due.Add(new BriefItem(t.Title, late ? L.F("{0} gün gecikti", (day.Date - t.DueDate.Value.Date).Days) : L.T("bugün"), Task: t, Urgent: late));
            }
        b.Sections.Add(new BriefSection("tasks", L.T("Görevler (bugün ve geciken)"), due));

        // ---- quotes
        var quoteItems = new List<BriefItem>();
        if (quotes != null)
        {
            foreach (var q in quotes.Quotes.Where(q => q.Status is QuoteStatus.New or QuoteStatus.InProgress && q.Deadline is { } d && d.Date <= day.Date.AddDays(2)).OrderBy(q => q.Deadline))
            {
                var d = q.Deadline!.Value.Date;
                var when = d < day.Date ? L.F("son tarih {0} gün geçti", (day.Date - d).Days) : d == day.Date ? L.T("son tarih bugün") : L.F("son tarih {0:d}", d);
                quoteItems.Add(new BriefItem(L.F("Teklif hazırla: {0} {1}", q.PartNumber, q.Revision.Length > 0 ? "rev " + q.Revision : "").Trim(), $"{q.Customer} · {q.Quantity} " + L.T("adet") + " · " + when, Quote: q, Urgent: d <= day.Date));
            }
            foreach (var q in quotes.Quotes.Where(q => q.Status == QuoteStatus.Sent && (day.Date - q.UpdatedAt.LocalDateTime.Date).TotalDays >= quoteWaitDays).OrderBy(q => q.UpdatedAt))
                quoteItems.Add(new BriefItem(L.F("Yanıt bekleniyor: {0}", q.PartNumber), L.F("{0} · {1} gündür cevap yok", q.Customer, (int)(day.Date - q.UpdatedAt.LocalDateTime.Date).TotalDays), Quote: q));
        }
        b.Sections.Add(new BriefSection("quotes", L.T("Teklifler"), quoteItems));

        // ---- unanswered sent mails
        var follow = new List<BriefItem>();
        if (followUps != null)
            foreach (var f in followUps.FollowUps.Where(f => !f.IsResolved && f.DueDate.LocalDateTime.Date <= day.Date).OrderBy(f => f.DueDate))
                follow.Add(new BriefItem(f.Subject, L.F("{0} · {1:d} tarihinde gönderildi, yanıt yok", f.Recipients, f.SentDate.LocalDateTime), Urgent: true));
        b.Sections.Add(new BriefSection("followups", L.T("Yanıt beklenen gönderilen mailler"), follow));

        // ---- new mail, sorted
        var mails = cache.GetInboxSince(b.Since).Where(m => m.Date.LocalDateTime < dayEnd && !m.IsSnoozed).ToList();
        b.NewMails = mails.Count; b.UnreadMails = mails.Count(m => !m.IsRead);
        var rfq = new List<BriefItem>(); var rev = new List<BriefItem>(); var order = new List<BriefItem>(); var other = new List<BriefItem>();
        foreach (var m in mails.OrderByDescending(m => m.Date))
        {
            var atts = m.HasAttachments ? cache.GetAttachments(m.AccountId, m.Folder, m.Uid) : new List<AttachmentInfo>();
            var item = new BriefItem(m.SubjectDisplay, $"{(m.FromName.Length > 0 ? m.FromName : m.FromAddress)} · {m.Date.LocalDateTime:g}" + (atts.Count > 0 ? " · " + L.F("{0} ek", atts.Count) : ""), Mail: m, Urgent: !m.IsRead && m.IsFlagged);
            var (isRfq, _) = QuoteStore.DetectRfq(m.Subject, m.Preview, $"{m.FromName} <{m.FromAddress}>", atts.Select(a => a.FileName).ToList());
            var text = m.Subject + " " + m.Preview;
            if (isRfq) rfq.Add(item);
            else if (Revision.IsMatch(text) || atts.Any(a => RevisionService.ParsePartAndRevision("", a.FileName).Revision.Length > 0 && PartHistory.KindOf(a.FileName) is "drawing" or "cad2d" or "model")) rev.Add(item);
            else if (Order.IsMatch(text)) order.Add(item);
            else if (!m.IsRead || m.IsFlagged) other.Add(item);
        }
        b.Sections.Add(new BriefSection("rfq", L.T("Yeni teklif talepleri"), rfq));
        b.Sections.Add(new BriefSection("revisions", L.T("Revizyonlar ve resim güncellemeleri"), rev));
        b.Sections.Add(new BriefSection("orders", L.T("Sipariş, teslimat, fatura"), order));
        b.Sections.Add(new BriefSection("other", L.T("Diğer okunmamış / bayraklı"), other));
        return b;
    }

    /// <summary>Plain-text version (clipboard, or the AI prompt when the user asks for a written summary).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.F("Sabah özeti – {0:D}", Day));
        sb.AppendLine(L.F("{0:g} sonrası {1} yeni mail ({2} okunmamış).", Since.LocalDateTime, NewMails, UnreadMails));
        foreach (var s in Sections.Where(s => s.Items.Count > 0))
        {
            sb.AppendLine().AppendLine($"{s.Title} ({s.Items.Count})");
            foreach (var i in s.Items) sb.AppendLine($"{(i.Urgent ? "! " : "- ")}{i.Title} — {i.Detail}");
        }
        return sb.ToString().TrimEnd();
    }
}

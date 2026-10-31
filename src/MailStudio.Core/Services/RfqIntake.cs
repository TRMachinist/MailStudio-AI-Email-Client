using System.Text;
using MailStudio.Models;
using MailStudio.Services.Costing;

namespace MailStudio.Services;

/// <summary>
/// Request-for-quote intake: new inbox mails that read as a request for quote become quote desk entries on arrival
/// (part, revision, quantity, material, deadline and the drawing files taken from the mail), and a polite
/// "we received your request" reply is prepared. The reply is only ever opened as a draft for the user to check
/// and send; nothing is sent automatically.
/// </summary>
public static class RfqIntake
{
    /// <summary>Adds the requests found in <paramref name="mails"/> to the quote desk; returns the new entries.
    /// A mail already on the desk (same message or same source) is skipped, so running it twice is harmless.</summary>
    public static List<QuoteItem> Process(IEnumerable<MailHeader> mails, MailCache cache, QuoteStore quotes, IEnumerable<string>? ownAddresses = null)
    {
        var own = new HashSet<string>((ownAddresses ?? Array.Empty<string>()).Select(a => a.Trim().ToLowerInvariant()));
        var created = new List<QuoteItem>();
        quotes.ReloadIfChanged();
        foreach (var m in mails)
        {
            if (!string.Equals(m.Folder, "INBOX", StringComparison.OrdinalIgnoreCase) || own.Contains(m.FromAddress.Trim().ToLowerInvariant())) continue;
            var source = $"{m.AccountId}|{m.Folder}|{m.Uid}";
            if (quotes.Quotes.Any(q => q.SourceMail == source || (!string.IsNullOrEmpty(m.MessageId) && q.SourceMessageId == m.MessageId))) continue;
            var content = cache.GetBody(m.AccountId, m.Folder, m.Uid);
            var body = content != null && content.Text.Length > 0 ? content.Text : content != null && content.Html.Length > 0 ? MailService.HtmlToText(content.Html) : m.Preview;
            var atts = m.HasAttachments ? cache.GetAttachments(m.AccountId, m.Folder, m.Uid).Select(a => a.FileName).ToList() : new List<string>();
            var (isRfq, item) = QuoteStore.DetectRfq(m.Subject, body, $"{m.FromName} <{m.FromAddress}>", atts);
            if (!isRfq) continue;
            item.SourceMail = source; item.SourceSubject = m.Subject; item.SourceMessageId = m.MessageId;
            if (string.IsNullOrEmpty(item.ContactEmail)) item.ContactEmail = m.FromAddress;
            if (item.Contact.Length == 0) item.Contact = m.FromName;
            item.Status = QuoteStatus.New; item.AutoCreated = true;
            item.CreatedAt = m.Date;
            item.Deadline ??= PromiseDate(m.Date.LocalDateTime.Date, 2);
            quotes.AddOrUpdate(item);
            created.Add(item);
        }
        return created;
    }

    /// <summary>The working day <paramref name="days"/> working days after <paramref name="from"/>.</summary>
    public static DateTime PromiseDate(DateTime from, int days)
    {
        var d = from.Date;
        while (days > 0) { d = d.AddDays(1); if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--; }
        return d;
    }

    /// <summary>Text of the acknowledgement: what was received (part, revision, quantity, files) and by when the
    /// quotation will follow. Written in the language of the request.</summary>
    public static string AckText(QuoteItem q, IEnumerable<string> files, CompanyInfo company, bool english, DateTime today)
    {
        var promise = q.Deadline is { } d && d.Date >= today ? d.Date : PromiseDate(today, 2);
        var c = english ? System.Globalization.CultureInfo.GetCultureInfo("en-GB") : System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var name = q.Contact.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var what = q.PartNumber.Length > 0
            ? (english ? $"{q.PartNumber}{(q.Revision.Length > 0 ? " rev " + q.Revision : "")}" : $"{q.PartNumber}{(q.Revision.Length > 0 ? " rev " + q.Revision : "")}")
            : (english ? "the requested part" : "talep ettiğiniz parça");
        var qty = q.Quantity > 0 ? (english ? $" ({q.Quantity} pcs)" : $" ({q.Quantity} adet)") : "";
        var sb = new StringBuilder();
        sb.AppendLine(english ? (name.Length > 0 ? $"Dear {name}," : "Hello,") : (q.Contact.Trim().Length > 0 ? $"Sayın {q.Contact.Trim()}," : "Merhaba,"));
        sb.AppendLine();
        sb.AppendLine(english
            ? $"Thank you for your request for quotation for {what}{qty}. We have received it and are reviewing the documents."
            : $"{what}{qty} için fiyat talebinizi aldık, teşekkür ederiz. Dokümanları inceliyoruz.");
        var list = files.Where(f => f.Length > 0).ToList();
        if (list.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(english ? "Documents received:" : "Alınan dokümanlar:");
            foreach (var f in list) sb.AppendLine("  - " + f);
        }
        sb.AppendLine();
        sb.AppendLine(english
            ? $"You will receive our quotation by {promise.ToString("dddd, d MMMM yyyy", c)}. If anything is missing on the drawings we will contact you before then."
            : $"Teklifimizi en geç {promise.ToString("d MMMM yyyy dddd", c)} gönderiyor olacağız. Resimlerde eksik bir bilgi olursa bu tarihten önce size dönüş yapacağız.");
        sb.AppendLine();
        sb.AppendLine(company.Signature.Length > 0 ? company.Signature : english ? "Best regards," : "Saygılarımızla,");
        if (company.Signature.Length == 0 && company.Name.Length > 0) sb.AppendLine(company.Name);
        return sb.ToString().TrimEnd();
    }

    /// <summary>English when the request reads as English (no Turkish letters, English request words).</summary>
    public static bool LooksEnglish(string subject, string body)
    {
        var t = (subject + " " + body).ToLowerInvariant();
        if (t.IndexOfAny("çğışöü".ToCharArray()) >= 0) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"\b(please|quote|quotation|request|pcs|regards|dear|hello|attached)\b");
    }
}

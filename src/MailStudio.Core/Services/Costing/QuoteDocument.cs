using System.Globalization;
using System.Net;
using System.Text;
using MailStudio.Models;

namespace MailStudio.Services.Costing;

/// <summary>Builds the quotation document (HTML, printed to PDF by the app) from a quote and the company settings.</summary>
public static class QuoteDocument
{
    /// <summary>Next free number "Q-yyyy-nnnn" over all quotes (per year).</summary>
    public static string NextNumber(IEnumerable<QuoteItem> quotes, DateTime today)
    {
        var prefix = $"Q-{today.Year}-";
        var max = quotes.Select(q => q.QuoteNumber).Where(n => n != null && n.StartsWith(prefix))
                        .Select(n => int.TryParse(n![prefix.Length..], out var i) ? i : 0).DefaultIfEmpty(0).Max();
        return prefix + (max + 1).ToString("0000", CultureInfo.InvariantCulture);
    }

    /// <summary>Safe file name for the PDF, e.g. "Q-2026-0007 NW-1042 rev C.pdf".</summary>
    public static string FileName(QuoteItem q)
    {
        var name = $"{q.QuoteNumber ?? "Quote"} {q.PartNumber}{(string.IsNullOrWhiteSpace(q.Revision) ? "" : " rev " + q.Revision)}".Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + ".pdf";
    }

    public static string BuildHtml(QuoteItem q, CostSettings s, DateTime date, bool includeBreakdown = false)
    {
        if (q.UnitPrice is not { } price) throw new InvalidOperationException(L.T("Teklifte birim fiyat yok; önce maliyet hesaplayın veya fiyatı girin."));
        string E(string? t) => WebUtility.HtmlEncode(t ?? "");
        string Br(string? t) => E(t).Replace("\n", "<br>");
        var cur = q.Currency ?? s.Currency;
        var c = s.Company;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>");
        sb.Append(@"@page{size:A4;margin:18mm 16mm}body{font-family:'Segoe UI',Arial,sans-serif;font-size:10.5pt;color:#1f2328}
h1{font-size:20pt;margin:0;color:#0F6CBD;letter-spacing:.5px}.muted{color:#5f6b7a}.top{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:2px solid #0F6CBD;padding-bottom:10px}
.logo{max-height:56px;max-width:220px}.co{text-align:right;line-height:1.45}.meta{display:flex;justify-content:space-between;margin:18px 0}
.box{border:1px solid #d0d7de;border-radius:6px;padding:10px 12px;min-width:44%}.box b{display:block;margin-bottom:4px;color:#0F6CBD;font-size:9pt;text-transform:uppercase;letter-spacing:.6px}
table{width:100%;border-collapse:collapse;margin-top:6px}th{background:#f3f6fa;text-align:left;font-size:9pt;color:#4a5562;padding:7px 8px;border-bottom:1px solid #d0d7de}
td{padding:8px;border-bottom:1px solid #eaeef2;vertical-align:top}td.r,th.r{text-align:right}.total td{font-weight:700;border-top:2px solid #1f2328;border-bottom:none;font-size:11.5pt}
.terms{margin-top:22px;line-height:1.55}.small{font-size:8.5pt}.sign{margin-top:36px}pre{font-family:inherit;white-space:pre-wrap;margin:0}");
        sb.Append("</style></head><body>");

        sb.Append("<div class='top'><div>");
        if (!string.IsNullOrWhiteSpace(c.LogoPath) && File.Exists(c.LogoPath))
        {
            var mime = Path.GetExtension(c.LogoPath).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
            sb.Append($"<img class='logo' src='data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(c.LogoPath))}'><br>");
        }
        sb.Append($"<h1>{E(L.T("FİYAT TEKLİFİ"))}</h1></div><div class='co'><b>{E(c.Name)}</b><br>{Br(c.Address)}");
        if (c.Phone.Length > 0) sb.Append("<br>" + E(c.Phone));
        if (c.Email.Length > 0) sb.Append("<br>" + E(c.Email));
        if (c.Web.Length > 0) sb.Append("<br>" + E(c.Web));
        if (c.TaxId.Length > 0) sb.Append($"<br><span class='muted'>{E(L.T("Vergi No"))}: {E(c.TaxId)}</span>");
        sb.Append("</div></div>");

        sb.Append("<div class='meta'><div class='box'>");
        sb.Append($"<b>{E(L.T("Müşteri"))}</b>{E(q.Customer)}");
        if (q.Contact.Length > 0) sb.Append("<br>" + E(q.Contact));
        if (!string.IsNullOrEmpty(q.ContactEmail)) sb.Append("<br>" + E(q.ContactEmail));
        sb.Append("</div><div class='box'>");
        sb.Append($"<b>{E(L.T("Teklif"))}</b>{E(L.T("No"))}: {E(q.QuoteNumber)}<br>{E(L.T("Tarih"))}: {date.ToString("d", L.Culture)}");
        sb.Append($"<br>{E(L.T("Geçerlilik"))}: {date.AddDays(s.QuoteValidityDays).ToString("d", L.Culture)}");
        if (!string.IsNullOrEmpty(q.SourceSubject)) sb.Append($"<br><span class='muted'>{E(L.T("Talebiniz"))}: {E(q.SourceSubject)}</span>");
        sb.Append("</div></div>");

        sb.Append("<table><tr>");
        sb.Append($"<th>{E(L.T("Parça"))}</th><th>{E(L.T("Rev"))}</th><th>{E(L.T("Malzeme"))}</th><th class='r'>{E(L.T("Adet"))}</th><th class='r'>{E(L.T("Birim fiyat"))}</th><th class='r'>{E(L.T("Tutar"))}</th></tr>");
        var qty = Math.Max(1, q.Quantity);
        sb.Append($"<tr><td><b>{E(q.PartNumber)}</b></td><td>{E(q.Revision)}</td><td>{E(q.Material)}</td><td class='r'>{qty.ToString("N0", L.Culture)}</td>");
        sb.Append($"<td class='r'>{E(CostEstimator.Money(price, cur))}</td><td class='r'>{E(CostEstimator.Money(Math.Round(price * qty, 2), cur))}</td></tr>");
        sb.Append($"<tr class='total'><td colspan='5'>{E(L.T("Toplam (KDV hariç)"))}</td><td class='r'>{E(CostEstimator.Money(Math.Round(price * qty, 2), cur))}</td></tr></table>");

        sb.Append("<div class='terms'>");
        if (q.RequestedDelivery is { } d) sb.Append($"<b>{E(L.T("İstenen teslim"))}:</b> {d.ToString("d", L.Culture)}<br>");
        if (s.DeliveryTerms.Length > 0) sb.Append($"<b>{E(L.T("Teslim şartları"))}:</b> {Br(s.DeliveryTerms)}<br>");
        if (s.PaymentTerms.Length > 0) sb.Append($"<b>{E(L.T("Ödeme"))}:</b> {Br(s.PaymentTerms)}<br>");
        sb.Append($"<span class='small muted'>{E(L.T("Fiyatlar teknik resim ve modelin bu revizyonu için geçerlidir; revizyon değişikliğinde teklif güncellenir."))}</span>");
        if (includeBreakdown && !string.IsNullOrWhiteSpace(q.CostBreakdown))
            sb.Append($"<h3>{E(L.T("Maliyet dökümü"))}</h3><pre class='small'>{E(q.CostBreakdown)}</pre>");
        sb.Append("</div>");
        if (c.Signature.Length > 0) sb.Append($"<div class='sign'>{Br(c.Signature)}</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}

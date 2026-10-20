using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MailStudio.Models;

namespace MailStudio.Services;

public sealed class QuoteStore
{
    readonly string _path;
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public List<QuoteItem> Quotes { get; private set; } = new();
    public event Action? Changed;

    DateTime _stamp;

    /// <summary>Re-reads quotes.json when the MCP agent server changed it (status updates), so the app never overwrites them.</summary>
    public bool ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(_path) || File.GetLastWriteTimeUtc(_path) == _stamp) return false;
            Load();
            Changed?.Invoke();
            return true;
        }
        catch (IOException) { return false; }
    }

    public QuoteStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "quotes.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = JsonFile.ReadOrBackup(_path);
                Quotes = JsonSerializer.Deserialize<List<QuoteItem>>(json, JsonOpts) ?? new();
                _stamp = File.GetLastWriteTimeUtc(_path);
                return;
            }
        }
        catch { }
        Quotes = new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(Quotes, JsonOpts);
            JsonFile.WriteAtomic(_path, json);
            _stamp = File.GetLastWriteTimeUtc(_path);
            Changed?.Invoke();
        }
        catch { }
    }

    public void AddOrUpdate(QuoteItem item)
    {
        ReloadIfChanged();
        item.UpdatedAt = DateTimeOffset.Now;
        var idx = Quotes.FindIndex(q => q.Id == item.Id);
        if (idx >= 0)
        {
            Quotes[idx] = item;
        }
        else
        {
            Quotes.Insert(0, item);
        }
        Save();
    }

    public bool Delete(string id)
    {
        ReloadIfChanged();
        var removed = Quotes.RemoveAll(q => q.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    public QuoteItem? Get(string id) => Quotes.FirstOrDefault(q => q.Id == id);

    /// <summary>
    /// Detects whether an email is a Request for Quotation (RFQ) and extracts initial quote fields.
    /// Supports Turkish and English keywords, regex pattern matching, and CAD attachment inspection.
    /// </summary>
    public static (bool IsRfq, QuoteItem Extracted) DetectRfq(
        string subject,
        string body,
        string from,
        IEnumerable<string>? attachmentNames = null)
    {
        var item = new QuoteItem();
        var fullText = (subject ?? "") + "\n" + (body ?? "");
        var cleanSubject = subject ?? "";
        var attList = attachmentNames?.ToList() ?? new List<string>();

        // 1. Contact and Customer from 'From' header
        // e.g. "Anna Schmidt <anna@schmidt-gmbh.de>" or "Hans Weber <h.weber@siemens.com>"
        var fromMatch = Regex.Match(from ?? "", @"^(?:""?([^""<]+)""?\s*)?<?([a-zA-Z0-9._%+-]+@([a-zA-Z0-9.-]+\.[a-zA-Z]{2,}))>?");
        if (fromMatch.Success)
        {
            var name = fromMatch.Groups[1].Value.Trim();
            var email = fromMatch.Groups[2].Value.Trim();
            var domain = fromMatch.Groups[3].Value.Trim();

            item.Contact = !string.IsNullOrEmpty(name) ? name : email;
            item.ContactEmail = email;

            // Guess company from domain (e.g. "schmidt-gmbh.de" -> "Schmidt Gmbh")
            var companyPart = domain.Split('.')[0];
            if (companyPart.Length > 2 && !companyPart.Equals("gmail", StringComparison.OrdinalIgnoreCase)
                                       && !companyPart.Equals("hotmail", StringComparison.OrdinalIgnoreCase)
                                       && !companyPart.Equals("outlook", StringComparison.OrdinalIgnoreCase)
                                       && !companyPart.Equals("yahoo", StringComparison.OrdinalIgnoreCase))
            {
                var cleanComp = Regex.Replace(companyPart, @"[-_]", " ");
                item.Customer = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cleanComp);
            }
        }
        else
        {
            item.Contact = from ?? "";
        }

        // Check if company is explicitly stated in body (e.g. "Firma: Acme Inc" or "Company: Acme Corp")
        var compExplicit = Regex.Match(fullText, @"(?:Firma|Company|Müşteri|Customer)[\s:#]+([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (compExplicit.Success && compExplicit.Groups[1].Value.Trim().Length > 1)
        {
            item.Customer = compExplicit.Groups[1].Value.Trim();
        }

        // 2. Part number and revision
        // First check attachment filenames for CAD parts (e.g. "NW-1042-revC.stp", "bracket-revB.stp", "flange-DN100.pdf")
        foreach (var att in attList.Where(a => Path.GetExtension(a).ToLowerInvariant() is ".stp" or ".step" or ".dxf" or ".dwg" or ".pdf" or ".igs" or ".iges" or ".x_t" or ".sldprt"))
        {
            var (part, rev) = RevisionService.ParsePartAndRevision("", att);
            if (!string.IsNullOrEmpty(part))
            {
                item.PartNumber = part;
                item.Revision = rev;
                break;
            }
        }

        // If no part from attachments, check subject and body
        if (string.IsNullOrEmpty(item.PartNumber))
        {
            var (partSub, revSub) = RevisionService.ParsePartAndRevision(cleanSubject);
            if (!string.IsNullOrEmpty(partSub))
            {
                item.PartNumber = partSub;
                item.Revision = revSub;
            }
            else
            {
                var partMatch = Regex.Match(fullText, @"(?:Parça\s*(?:No|Numarası|Kodu)?|Part\s*(?:No|Number|Code)?|P\/N|PN)[\s:#]+([A-Za-z0-9_\-\.\/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (partMatch.Success)
                {
                    item.PartNumber = partMatch.Groups[1].Value.Trim();
                }
                else
                {
                    // Look for common engineering code patterns like NW-1042, ABC-123, FLANGE-DN100
                    var codeMatch = Regex.Match(fullText, @"\b([A-Z0-9]{2,}[-_][A-Z0-9]{2,}(?:[-_][A-Za-z0-9]+)*)\b");
                    if (codeMatch.Success)
                    {
                        item.PartNumber = codeMatch.Groups[1].Value.Trim();
                    }
                }
            }
        }

        // If revision is still empty, look for revision keywords
        if (string.IsNullOrEmpty(item.Revision))
        {
            var revMatch = Regex.Match(fullText, @"(?:Rev(?:izyon)?|Revision)[\s.:#-]*([A-Za-z0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (revMatch.Success)
            {
                item.Revision = revMatch.Groups[1].Value.Trim();
            }
        }

        // 3. Quantity (Adet)
        var qtyMatch = Regex.Match(fullText, @"(?:Adet|Miktar|Quantity|Qty)[ \t:#]*(\d{1,3}(?:[.,]\d{3})+|\d{1,7})(?![\d.,]\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (qtyMatch.Success)
        {
            var clean = Regex.Replace(qtyMatch.Groups[1].Value, @"[^\d]", "");
            if (int.TryParse(clean, out var q) && q > 0) item.Quantity = q;
        }
        if (item.Quantity == 0)
        {
            var suffixQty = Regex.Match(fullText, @"\b(\d+)\s*(?:adet|pcs|pieces|ea|stk|adetlik)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (suffixQty.Success && int.TryParse(suffixQty.Groups[1].Value, out var q) && q > 0)
            {
                item.Quantity = q;
            }
        }

        // 4. Material (Malzeme)
        var matMatch = Regex.Match(fullText, @"(?:Malzeme|Material)[\s:#]+([^\r\n,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matMatch.Success)
        {
            item.Material = matMatch.Groups[1].Value.Trim();
        }
        else
        {
            var knownMat = Regex.Match(fullText, @"\b(Alüminyum\s*\d*[-A-Za-z0-9]*|Aluminium\s*\d*[-A-Za-z0-9]*|Aluminum\s*\d*[-A-Za-z0-9]*|Paslanmaz\s*(?:Çelik)?(?:\s*1\.\d+|\s*AISI\s*\d+)?|Stainless\s*Steel(?:\s*304|\s*316|\s*1\.\d+)?|AISI\s*\d+|1\.\d{4}|St\s*52(?:-3)?|St\s*37|Çelik|Steel|Delrin|POM|Pirinç|Brass|Titanyum|Titanium)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (knownMat.Success)
            {
                item.Material = knownMat.Groups[1].Value.Trim();
            }
        }

        // 5. Quote Deadline (Son Teklif Tarihi)
        var deadlineMatch = Regex.Match(fullText, @"(?:Son\s*Teklif(?:\s*Tarihi)?|Teklif\s*Tarihi|Son\s*Tarih|Deadline|Quote\s*Due|Due\s*Date)[\s:#]+(\d{4}[./-]\d{1,2}[./-]\d{1,2}|\d{1,2}[./-]\d{1,2}[./-]\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (deadlineMatch.Success && TryParseFlexibleDate(deadlineMatch.Groups[1].Value, out var dline))
        {
            item.Deadline = dline;
        }

        // 6. Requested Delivery (İstenen Teslim Tarihi / Termin)
        var delivMatch = Regex.Match(fullText, @"(?:İstenen\s*Teslim(?:\s*Tarihi)?|Teslim\s*Tarihi|Termin|Delivery(?:\s*Date)?|Requested\s*Delivery|Lead\s*Time)[\s:#]+(\d{4}[./-]\d{1,2}[./-]\d{1,2}|\d{1,2}[./-]\d{1,2}[./-]\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (delivMatch.Success && TryParseFlexibleDate(delivMatch.Groups[1].Value, out var delivDate))
        {
            item.RequestedDelivery = delivDate;
        }

        // 7. RFQ Confidence Scoring
        var score = 0;
        // 'İ' first: invariant lower-casing would leave "TEKLİF" as "teklİf"
        var subLower = cleanSubject.Replace('İ', 'i').ToLowerInvariant();
        var bodyLower = (body ?? "").Replace('İ', 'i').ToLowerInvariant();

        // Turkish & English quote keywords in subject
        var subjectKeyword = Regex.IsMatch(subLower, @"\b(teklif|fiyat|fiyatlandırma|quotation|quote|rfq|pricing|price\s*inquiry|angebot)\b", RegexOptions.CultureInvariant);
        if (subjectKeyword) score += 45;

        // Keywords in body
        var bodyKeyword = Regex.IsMatch(bodyLower, @"\b(teklif|fiyat\s*teklifi|fiyat\s*rica|teklif\s*rica|fiyatlandırma|quotation|quote|rfq|request\s*for\s*quote|request\s*for\s*quotation|please\s*quote|price\s*request)\b", RegexOptions.CultureInvariant);
        if (bodyKeyword) score += 30;

        if (item.Quantity > 0) score += 20;
        if (!string.IsNullOrEmpty(item.PartNumber)) score += 20;
        if (!string.IsNullOrEmpty(item.Material)) score += 15;
        if (item.Deadline.HasValue) score += 15;

        // Technical attachments bonus
        var hasCad = attList.Any(a =>
        {
            var ext = Path.GetExtension(a).ToLowerInvariant();
            return ext is ".stp" or ".step" or ".dxf" or ".dwg" or ".pdf" or ".igs" or ".iges" or ".x_t" or ".sldprt";
        });
        if (hasCad) score += 25;

        // a quote word is required, plus enough evidence (quantity, part, drawing...)
        var isRfq = (subjectKeyword || bodyKeyword) && score >= 65;

        // Populate brief notes from subject if notes empty
        if (string.IsNullOrEmpty(item.Notes))
        {
            item.Notes = cleanSubject.Trim();
        }

        return (isRfq, item);
    }

    static bool TryParseFlexibleDate(string s, out DateTime dt)
    {
        var formats = new[]
        {
            "d.M.yyyy", "dd.MM.yyyy", "d/M/yyyy", "dd/MM/yyyy",
            "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "yyyy.MM.dd",
            "d.M.yy", "dd.MM.yy"
        };
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
    }

    /// <summary>
    /// Checks outgoing emails with attachments. If the email is a response to an RFQ inquiry,
    /// marks the corresponding quote item status as 'Sent' (Gönderildi).
    /// </summary>
    public bool CheckSentMail(string toEmail, string? inReplyTo, string? subject, int attachmentCount)
    {
        ReloadIfChanged();
        if (attachmentCount <= 0) return false;

        var modified = false;
        var toClean = (toEmail ?? "").Trim().ToLowerInvariant();
        var subClean = (subject ?? "").Trim().ToLowerInvariant();

        foreach (var q in Quotes)
        {
            if (q.Status is not (QuoteStatus.New or QuoteStatus.InProgress))
                continue;

            var match = false;

            // Match 1: In-Reply-To matches the source message ID of the quote
            if (!string.IsNullOrEmpty(inReplyTo) && !string.IsNullOrEmpty(q.SourceMessageId)
                && string.Equals(inReplyTo.Trim('<', '>'), q.SourceMessageId.Trim('<', '>'), StringComparison.OrdinalIgnoreCase))
            {
                match = true;
            }

            // Match 2: Recipient matches quote ContactEmail and subject references part number
            if (!match && !string.IsNullOrEmpty(q.ContactEmail) && !string.IsNullOrEmpty(q.PartNumber))
            {
                if (toClean.Contains(q.ContactEmail.ToLowerInvariant()) && subClean.Contains(q.PartNumber.ToLowerInvariant()))
                {
                    match = true;
                }
            }

            if (match)
            {
                q.Status = QuoteStatus.Sent;
                q.UpdatedAt = DateTimeOffset.Now;
                modified = true;
            }
        }

        if (modified) Save();
        return modified;
    }

    /// <summary>
    /// Exports quote items to an Excel (.xlsx) workbook using ClosedXML with professional formatting.
    /// </summary>
    public static void ExportToExcel(IEnumerable<QuoteItem> quotes, string filePath)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add(L.T("Teklif Masası"));

        // Headers
        var headers = new[]
        {
            L.T("Durum"),
            L.T("Parça No"),
            L.T("Rev"),
            L.T("Adet"),
            L.T("Malzeme"),
            L.T("Müşteri"),
            L.T("İlgili Kişi"),
            L.T("Son Teklif Tarihi"),
            L.T("İstenen Teslim"),
            L.T("Notlar"),
            L.T("Kaynak Konu"),
            L.T("Eklenme Tarihi")
        };

        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F6CBD"); // Accent blue
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D1D5DB");
        }

        var row = 2;
        foreach (var q in quotes)
        {
            ws.Cell(row, 1).Value = q.StatusDisplayName;
            ws.Cell(row, 2).Value = q.PartNumber;
            ws.Cell(row, 3).Value = q.Revision;
            ws.Cell(row, 4).Value = q.Quantity > 0 ? q.Quantity : "";
            ws.Cell(row, 5).Value = q.Material;
            ws.Cell(row, 6).Value = q.Customer;
            ws.Cell(row, 7).Value = q.Contact;
            ws.Cell(row, 8).Value = q.Deadline.HasValue ? q.Deadline.Value.ToString("yyyy-MM-dd") : "";
            ws.Cell(row, 9).Value = q.RequestedDelivery.HasValue ? q.RequestedDelivery.Value.ToString("yyyy-MM-dd") : "";
            ws.Cell(row, 10).Value = q.Notes;
            ws.Cell(row, 11).Value = q.SourceSubject ?? "";
            ws.Cell(row, 12).Value = q.CreatedAt.ToString("yyyy-MM-dd HH:mm");

            // Format numbers and alignments
            ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Zebra striping
            if (row % 2 == 1)
            {
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            }

            // Highlight overdue or urgent quotes
            if (q.IsDeadlineOverdue)
            {
                ws.Cell(row, 8).Style.Font.FontColor = XLColor.FromHtml("#DC2626"); // Red
                ws.Cell(row, 8).Style.Font.Bold = true;
            }

            for (var c = 1; c <= headers.Length; c++)
            {
                ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                ws.Cell(row, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E5E7EB");
            }

            row++;
        }

        ws.Columns().AdjustToContents();
        workbook.SaveAs(filePath);
    }
}

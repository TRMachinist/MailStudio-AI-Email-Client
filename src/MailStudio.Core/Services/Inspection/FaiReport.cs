using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;

namespace MailStudio.Services.Inspection;

/// <summary>One ballooned characteristic of a drawing (AS9102 Form 3 row).</summary>
public sealed class Characteristic
{
    public int Id { get; set; }
    public int Page { get; set; }
    public string Zone { get; set; } = "";
    /// <summary>linear, diameter, radius, angle, thread, roughness, note, manual</summary>
    public string Type { get; set; } = "linear";
    public string Requirement { get; set; } = "";
    public double? Nominal { get; set; }
    /// <summary>Upper deviation (+), lower deviation (−, stored positive).</summary>
    public double? Upper { get; set; }
    public double? Lower { get; set; }
    public string Unit { get; set; } = "mm";
    public int Quantity { get; set; } = 1;
    public string? Fit { get; set; }
    public string ToleranceSource { get; set; } = "";
    /// <summary>Read with OCR or with low confidence: the inspector must confirm it on the drawing.</summary>
    public bool Review { get; set; }
    public string Source { get; set; } = "text";
    public string Notes { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    /// <summary>Balloon centre and radius in page points (top-left origin), null until placed.</summary>
    public double[]? Balloon { get; set; }

    public double? Min => Type == "roughness" ? (Upper != null ? 0 : null) : Nominal is { } n && Lower is { } l && Upper != null ? Math.Round(n - l, 4) : null;
    public double? Max => Nominal is { } n && Upper is { } u ? Math.Round(Type == "roughness" ? u : n + u, 4) : null;

    public string TypeText => Type switch
    {
        "linear" => L.T("Ölçü"), "diameter" => L.T("Çap"), "radius" => L.T("Radyus"), "angle" => L.T("Açı"),
        "thread" => L.T("Diş"), "roughness" => L.T("Yüzey pürüzlülüğü"), "note" => L.T("Not"), "manual" => L.T("Elle eklendi"), _ => Type
    };

    public string ToleranceText
    {
        get
        {
            if (Type == "roughness" && Upper is { } r) return "≤ " + Num(r);
            if (Upper is { } u && Lower is { } l) return u == l ? "± " + Num(u) : $"+{Num(u)} / −{Num(l)}";
            return Fit ?? "";
        }
    }

    static string Num(double v) => v.ToString("0.####", CultureInfo.CurrentCulture);

    public object ToWorker() => new
    {
        id = Id, page = Page, x = X, y = Y, w = W, h = H, balloon = Balloon
    };

    public static List<Characteristic> FromExtract(JsonElement result)
    {
        static double? D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var list = new List<Characteristic>();
        foreach (var i in result.GetProperty("items").EnumerateArray())
        {
            list.Add(new Characteristic
            {
                Id = i.GetProperty("id").GetInt32(), Page = i.GetProperty("page").GetInt32(), Zone = S(i, "zone"), Type = S(i, "type"),
                Requirement = S(i, "requirement"), Nominal = D(i, "nominal"), Upper = D(i, "upper"), Lower = D(i, "lower"),
                Unit = S(i, "unit") is { Length: > 0 } u ? u : "mm", Quantity = (int)(D(i, "quantity") ?? 1), Fit = S(i, "fit") is { Length: > 0 } f ? f : null,
                ToleranceSource = S(i, "tolerance_source"), Review = i.TryGetProperty("review", out var rv) && rv.ValueKind == JsonValueKind.True,
                Source = S(i, "source"), X = D(i, "x") ?? 0, Y = D(i, "y") ?? 0, W = D(i, "w") ?? 0, H = D(i, "h") ?? 0,
                Balloon = i.TryGetProperty("balloon", out var b) && b.ValueKind == JsonValueKind.Array ? b.EnumerateArray().Select(v => v.GetDouble()).ToArray() : null
            });
        }
        return list;
    }

    /// <summary>Numbers again in page / reading order after the inspector removed or added items.</summary>
    public static void Renumber(IList<Characteristic> items)
    {
        var ordered = items.OrderBy(i => i.Page).ThenBy(i => Math.Round(i.Y / 18)).ThenBy(i => i.X).ToList();
        for (var n = 0; n < ordered.Count; n++) ordered[n].Id = n + 1;
    }
}

/// <summary>Form 1 header fields of the first article inspection report.</summary>
public sealed class FaiHeader
{
    public string PartNumber { get; set; } = "";
    public string PartName { get; set; } = "";
    public string Revision { get; set; } = "";
    public string DrawingNumber { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Customer { get; set; } = "";
    public string ReportNumber { get; set; } = "";
    public string DrawingFile { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Today;
}

/// <summary>AS9102 workbook: Form 1 (part number accountability) and Form 3 (characteristic accountability, with
/// pass/fail formulas on the measured values).</summary>
public static class FaiReport
{
    static readonly XLColor Head = XLColor.FromHtml("#0F6CBD");

    public static void ExportAs9102(string path, FaiHeader h, IEnumerable<Characteristic> items)
    {
        using var wb = new XLWorkbook();
        var f1 = wb.Worksheets.Add("Form 1");
        f1.Cell(1, 1).Value = L.T("AS9102 Form 1 – Parça numarası hesap verebilirliği");
        f1.Cell(1, 1).Style.Font.Bold = true; f1.Cell(1, 1).Style.Font.FontSize = 14;
        var rows = new (string Label, string Value)[]
        {
            ("1. " + L.T("Parça numarası"), h.PartNumber), ("2. " + L.T("Parça adı"), h.PartName),
            ("3. " + L.T("Seri numarası"), ""), ("4. " + L.T("FAI rapor numarası"), h.ReportNumber),
            ("5. " + L.T("Parça revizyonu"), h.Revision), ("6. " + L.T("Resim numarası"), h.DrawingNumber),
            ("7. " + L.T("Resim revizyonu"), h.Revision), ("9. " + L.T("Üretici adı"), h.Organization),
            ("12. " + L.T("Müşteri"), h.Customer), ("14. " + L.T("FAI türü"), L.T("Tam FAI")),
            (L.T("Resim dosyası"), h.DrawingFile), (L.T("Tarih"), h.Date.ToString("d", L.Culture)),
        };
        for (var r = 0; r < rows.Length; r++)
        {
            f1.Cell(r + 3, 1).Value = rows[r].Label; f1.Cell(r + 3, 1).Style.Font.Bold = true;
            f1.Cell(r + 3, 2).Value = rows[r].Value;
        }
        f1.Range(3, 1, rows.Length + 2, 2).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        f1.Range(3, 1, rows.Length + 2, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        f1.Column(1).Width = 30; f1.Column(2).Width = 48;

        var f3 = wb.Worksheets.Add("Form 3");
        f3.Cell(1, 1).Value = L.T("AS9102 Form 3 – Karakteristik hesap verebilirliği");
        f3.Cell(1, 1).Style.Font.Bold = true; f3.Cell(1, 1).Style.Font.FontSize = 14;
        f3.Cell(2, 1).Value = $"{L.T("Parça numarası")}: {h.PartNumber}   {L.T("Rev")}: {h.Revision}   {L.T("FAI rapor numarası")}: {h.ReportNumber}";
        var headers = new[]
        {
            "5. " + L.T("Kar. No"), "6. " + L.T("Referans konumu"), "7. " + L.T("Kar. tanımlayıcı"), "8. " + L.T("Gereklilik"),
            L.T("Nominal"), L.T("Alt sınır"), L.T("Üst sınır"), L.T("Birim"), L.T("Adet"),
            "9. " + L.T("Sonuç (ölçülen)"), L.T("Değerlendirme"), "10. " + L.T("Tasarlanmış / kalifiye takım"),
            "11. " + L.T("Uygunsuzluk numarası"), "14. " + L.T("Notlar")
        };
        const int hr = 4;
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = f3.Cell(hr, c + 1);
            cell.Value = headers[c]; cell.Style.Font.Bold = true; cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = Head; cell.Style.Alignment.WrapText = true; cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        var row = hr + 1;
        foreach (var it in items.OrderBy(i => i.Id))
        {
            f3.Cell(row, 1).Value = it.Id;
            f3.Cell(row, 2).Value = $"{L.T("Sayfa")} {it.Page + 1} · {it.Zone}";
            f3.Cell(row, 3).Value = it.TypeText;
            f3.Cell(row, 4).Value = it.Requirement + (it.ToleranceSource.StartsWith("ISO") ? $"  ({it.ToleranceSource} ±{it.Upper?.ToString("0.###", CultureInfo.InvariantCulture)})" : "");
            if (it.Nominal is { } n) f3.Cell(row, 5).Value = n;
            if (it.Min is { } mn) f3.Cell(row, 6).Value = mn;
            if (it.Max is { } mx) f3.Cell(row, 7).Value = mx;
            f3.Cell(row, 8).Value = it.Unit;
            f3.Cell(row, 9).Value = it.Quantity;
            // OK / NOK as soon as the inspector types the measured value; empty when the limits are unknown
            f3.Cell(row, 11).FormulaA1 = $"IF(OR(J{row}=\"\",F{row}=\"\",G{row}=\"\"),\"\",IF(AND(J{row}>=F{row},J{row}<=G{row}),\"OK\",\"NOK\"))";
            var notes = new List<string>();
            if (it.Review) notes.Add(L.T("OCR ile okundu – resimde doğrulayın"));
            if (it.Fit != null && it.Upper == null) notes.Add(L.F("Geçme {0}: sınırları tablodan girin", it.Fit));
            if (it.Notes.Length > 0) notes.Add(it.Notes);
            f3.Cell(row, 14).Value = string.Join("; ", notes);
            if (it.Review) f3.Cell(row, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4CE");
            row++;
        }
        var table = f3.Range(hr, 1, Math.Max(hr, row - 1), headers.Length);
        table.Style.Border.InsideBorder = XLBorderStyleValues.Thin; table.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        if (row > hr + 1)
        {
            var eval = f3.Range(hr + 1, 11, row - 1, 11);
            eval.AddConditionalFormat().WhenEquals("\"NOK\"").Fill.SetBackgroundColor(XLColor.FromHtml("#FDE7E9")).Font.SetFontColor(XLColor.FromHtml("#C50F1F"));
            eval.AddConditionalFormat().WhenEquals("\"OK\"").Fill.SetBackgroundColor(XLColor.FromHtml("#DFF6DD")).Font.SetFontColor(XLColor.FromHtml("#0E700E"));
            f3.Range(hr + 1, 10, row - 1, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F9FF");
        }
        var widths = new[] { 8, 16, 16, 34, 10, 10, 10, 7, 6, 14, 13, 18, 16, 36 };
        for (var c = 0; c < widths.Length; c++) f3.Column(c + 1).Width = widths[c];
        f3.Row(hr).Height = 32;
        f3.SheetView.FreezeRows(hr);
        f3.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        f3.PageSetup.FitToPages(1, 0);
        wb.Worksheets.Worksheet("Form 3").SetTabActive();
        var tmp = path + ".tmp.xlsx";
        wb.SaveAs(tmp);
        SafeFile.Move(tmp, path, true);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace MailStudio.Services;

/// <summary>A table found on a PDF page (rows of cell texts).</summary>
public sealed class PdfTable
{
    public int Page { get; init; }
    public List<List<string>> Rows { get; init; } = new();
    public bool Review { get; init; }
    public bool Ruled { get; init; }
    public int Columns => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
    public string Title => L.F("Sayfa {0} · {1} satır × {2} sütun", Page + 1, Rows.Count, Columns);

    public static List<PdfTable> FromWorker(JsonElement result) =>
        result.GetProperty("tables").EnumerateArray().Select(t => new PdfTable
        {
            Page = t.GetProperty("page").GetInt32(),
            Rows = t.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString() ?? "").ToList()).ToList(),
            Review = t.TryGetProperty("review", out var rv) && rv.ValueKind == JsonValueKind.True,
            Ruled = t.TryGetProperty("ruled", out var ru) && ru.ValueKind == JsonValueKind.True,
        }).ToList();

    /// <summary>The first row is a header when it has no numbers while the rows below do.</summary>
    public bool HasHeader => Rows.Count > 1 && !Rows[0].Any(c => PdfTableExport.TryNumber(c, out _))
                             && Rows.Skip(1).Any(r => r.Any(c => PdfTableExport.TryNumber(c, out _)));
}

/// <summary>Writes PDF tables to an Excel workbook: one sheet per table, numbers as numbers where a column is numeric.</summary>
public static class PdfTableExport
{
    static readonly Regex Plain = new(@"^[-+]?\d+(?:[.,]\d+)?$", RegexOptions.CultureInvariant);
    static readonly Regex Grouped = new(@"^[-+]?\d{1,3}(?:([.,])\d{3})+(?:[.,]\d+)?$", RegexOptions.CultureInvariant);

    /// <summary>Number in a cell: "16", "1,5", "1.250" (one separator = decimal, as in drawings), "1.234.567",
    /// "1.234,50", "1,234.50". Codes with leading zeros ("007") stay text.</summary>
    public static bool TryNumber(string s, out double v)
    {
        v = 0; s = s.Trim().Replace(" ", "");
        if (s.Length == 0 || (s.Length > 1 && s[0] == '0' && char.IsDigit(s[1])) || (s.Length > 2 && s[0] is '-' or '+' && s[1] == '0' && char.IsDigit(s[2]))) return false;
        if (Plain.IsMatch(s)) return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        var m = Grouped.Match(s);
        if (!m.Success) return false;
        var group = m.Groups[1].Value[0];
        var last = s.LastIndexOfAny(new[] { '.', ',' });
        var dec = s[last] != group ? s[last] : '\0';
        if (dec == '\0' && s.Count(ch => ch == group) < 2) return false;   // "1.234" handled by Plain as a decimal
        var digits = new string(s.Where(ch => ch != group).ToArray()).Replace(dec == '\0' ? "\0" : dec.ToString(), ".");
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    public static void ToExcel(string path, IReadOnlyList<PdfTable> tables, string source)
    {
        if (tables.Count == 0) throw new InvalidOperationException(L.T("Aktarılacak tablo yok."));
        using var wb = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < tables.Count; n++)
        {
            var t = tables[n];
            var name = L.F("S{0} Tablo {1}", t.Page + 1, n + 1);
            while (!used.Add(name)) name += "_";
            var ws = wb.Worksheets.Add(name.Length > 31 ? name[..31] : name);
            var header = t.HasHeader;
            var cols = t.Columns;
            // a column is numeric when 70 % of its body cells are numbers: "8.8" in a material column stays text
            var numeric = Enumerable.Range(0, cols).Select(c =>
            {
                var body = t.Rows.Skip(header ? 1 : 0).Select(r => c < r.Count ? r[c] : "").Where(x => x.Trim().Length > 0).ToList();
                return body.Count > 0 && body.Count(x => TryNumber(x, out _)) >= Math.Ceiling(body.Count * 0.7);
            }).ToArray();
            for (var r = 0; r < t.Rows.Count; r++)
                for (var c = 0; c < cols; c++)
                {
                    var text = c < t.Rows[r].Count ? t.Rows[r][c] : "";
                    var cell = ws.Cell(r + 1, c + 1);
                    if (!(header && r == 0) && numeric[c] && TryNumber(text, out var v)) cell.Value = v;
                    else cell.Value = text;
                }
            var range = ws.Range(1, 1, Math.Max(1, t.Rows.Count), Math.Max(1, cols));
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin; range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            if (header)
            {
                var h = ws.Range(1, 1, 1, cols);
                h.Style.Font.Bold = true; h.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE9F7");
                ws.SheetView.FreezeRows(1);
                ws.Range(1, 1, t.Rows.Count, cols).SetAutoFilter();
            }
            ws.Columns(1, cols).AdjustToContents(8, 60);
            var note = ws.Cell(t.Rows.Count + 2, 1);
            note.Value = L.F("Kaynak: {0}, sayfa {1}", source, t.Page + 1) + (t.Review ? "  ·  " + L.T("OCR ile okundu: değerleri PDF'te doğrulayın.") : "");
            note.Style.Font.Italic = true; note.Style.Font.FontColor = XLColor.Gray;
            if (t.Review && t.Rows.Count > (header ? 1 : 0)) ws.Range(header ? 2 : 1, 1, t.Rows.Count, Math.Max(1, cols)).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF8DC");
        }
        var tmp = path + ".tmp.xlsx";
        wb.SaveAs(tmp);
        SafeFile.Move(tmp, path, true);
    }
}

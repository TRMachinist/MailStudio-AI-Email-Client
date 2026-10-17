using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;

namespace MailStudio.Services;

/// <summary>
/// Turns an attachment into compact text an AI can reason about: drawing texts and dimensions, STEP sizes, volumes and
/// PMI, Excel cells with addresses, Word/PowerPoint text. Used by skills in the app and by the MCP server for agents.
/// The source file is only read, never changed.
/// </summary>
public static class AttachmentExtractor
{
    public const int MaxChars = 40000;

    static readonly string[] Technical = { ".pdf", ".step", ".stp", ".dxf", ".dwg" };
    static readonly string[] Plain = { ".txt", ".csv", ".nc", ".tap", ".mpf", ".spf", ".gcode", ".iso", ".json", ".xml", ".md", ".log", ".eml" };

    public static bool CanExtract(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return Technical.Contains(ext) || Plain.Contains(ext) || ext is ".xlsx" or ".xlsm" or ".docx" or ".pptx";
    }

    /// <summary>Returns a text block (JSON for technical files). The worker is created on demand when needed.</summary>
    public static async Task<string> ExtractAsync(string path, Func<TechnicalWorker> worker, bool ocr = true, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        string text = ext switch
        {
            ".pdf" => Json(await worker().CallAsync("extract_pdf", new { path, ocr }, ct)),
            ".step" or ".stp" => Json(await worker().CallAsync("extract_step", new { path }, ct)),
            ".dxf" or ".dwg" => Json(await worker().CallAsync("extract_dxf", new { path }, ct)),
            ".xlsx" or ".xlsm" => await Task.Run(() => Excel(path), ct),
            ".docx" => await Task.Run(() => Word(path), ct),
            ".pptx" => await Task.Run(() => Slides(path), ct),
            _ when Plain.Contains(ext) => await File.ReadAllTextAsync(path, ct),
            _ => throw new NotSupportedException(L.F("Bu dosya türü okunamıyor: {0}", ext)),
        };
        return text.Length > MaxChars ? text[..MaxChars] + "\n[...truncated]" : text;
    }

    static string Json(JsonElement e) => JsonSerializer.Serialize(e, new JsonSerializerOptions
    { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Every used cell as "Sheet!A1 = value" (formulas shown after the value), so edits can name exact cells.</summary>
    public static string Excel(string path, int maxRows = 400, int maxCols = 40)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(stream);
        try { wb.RecalculateAllFormulas(); } catch { }
        var sb = new StringBuilder();
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            sb.AppendLine($"# Sheet \"{ws.Name}\"" + (used == null ? " (empty)" : $" range {used.RangeAddress.ToStringRelative()}"));
            if (used == null) continue;
            int r0 = used.FirstRow().RowNumber(), r1 = Math.Min(used.LastRow().RowNumber(), r0 + maxRows - 1);
            int c0 = used.FirstColumn().ColumnNumber(), c1 = Math.Min(used.LastColumn().ColumnNumber(), c0 + maxCols - 1);
            for (int r = r0; r <= r1; r++)
            {
                var cells = new List<string>();
                for (int c = c0; c <= c1; c++)
                {
                    var cell = ws.Cell(r, c);
                    if (cell.IsEmpty()) continue;
                    string v;
                    try { v = cell.GetFormattedString(); } catch { v = cell.CachedValue.ToString() ?? ""; }
                    if (cell.HasFormula) v += $" (={cell.FormulaA1})";
                    cells.Add($"{cell.Address.ToStringRelative()}={v.Replace('\n', ' ')}");
                }
                if (cells.Count > 0) sb.AppendLine(string.Join(" | ", cells));
            }
            if (used.LastRow().RowNumber() > r1) sb.AppendLine($"[... {used.LastRow().RowNumber() - r1} more rows]");
        }
        return sb.ToString();
    }

    public static string Word(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return "";
        var sb = new StringBuilder();
        foreach (var el in body.Elements())
        {
            if (el is DocumentFormat.OpenXml.Wordprocessing.Table t)
            {
                foreach (var row in t.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>())
                    sb.AppendLine("| " + string.Join(" | ", row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Select(c => c.InnerText.Trim())) + " |");
            }
            else if (el.InnerText.Trim() is { Length: > 0 } text) sb.AppendLine(text);
        }
        return sb.ToString();
    }

    public static string Slides(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var part = doc.PresentationPart;
        var ids = part?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToList() ?? new();
        var sb = new StringBuilder(); int n = 0;
        foreach (var id in ids)
        {
            n++;
            if (part!.GetPartById(id.RelationshipId!) is not SlidePart slide) continue;
            sb.AppendLine($"# Slide {n}");
            foreach (var p in slide.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>())
                if (p.InnerText.Trim() is { Length: > 0 } text) sb.AppendLine(text);
            var notes = slide.NotesSlidePart?.NotesSlide?.InnerText.Trim();
            if (!string.IsNullOrEmpty(notes)) sb.AppendLine("Notes: " + notes);
        }
        return sb.ToString();
    }
}

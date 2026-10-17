using System.Net;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlToOpenXml;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace MailStudio.Views.Editors;

/// <summary>
/// Word body ↔ editable HTML. Reading: paragraph styles (headings), alignment, bold/italic/underline/strikethrough, color, size,
/// highlight, superscript/subscript, links, lists, tables (merged cells), inline images.
/// Writing: the source document is copied and only the body is regenerated from HTML (HtmlToOpenXml, MIT); headers/footers, styles,
/// page setup and numbering definitions stay as in the source.
/// </summary>
public static class DocxHtml
{
    public sealed record Result(string Html, double PageWidthPx, Thickness Margins, List<string> Warnings);
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom);

    public static Result Read(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException(L.T("Belgede ana bölüm yok."));
        var body = main.Document?.Body ?? throw new InvalidDataException(L.T("Belge gövdesi boş."));
        var warnings = new List<string>();
        void Warn(bool cond, string text) { if (cond && !warnings.Contains(text)) warnings.Add(text); }
        Warn(body.Descendants<TextBoxContent>().Any(), L.T("metin kutuları"));
        Warn(body.Descendants<DocumentFormat.OpenXml.Vml.Shape>().Any() || body.Descendants().Any(e => e.LocalName == "wsp"), L.T("şekiller"));
        Warn(body.Descendants<FieldCode>().Any(f => f.Text.Contains("TOC")), L.T("içindekiler tablosu"));
        Warn(body.Descendants<FootnoteReference>().Any() || body.Descendants<EndnoteReference>().Any(), "dipnotlar");
        Warn(body.Descendants<CommentRangeStart>().Any(), L.T("açıklamalar (yorumlar)"));
        Warn(body.Descendants<InsertedRun>().Any() || body.Descendants<DeletedRun>().Any(), L.T("izlenen değişiklikler"));
        Warn(body.Descendants().Any(e => e.LocalName == "oMath"), "denklemler");
        Warn(body.Descendants<DW.Anchor>().Any(), L.T("metne göre konumlu (yüzen) resimler"));
        Warn(body.Elements<SectionProperties>().Count() + body.Descendants<ParagraphProperties>().Count(p => p.SectionProperties != null) > 1, L.T("birden çok bölüm"));

        var numbering = main.NumberingDefinitionsPart?.Numbering;
        var styles = main.StyleDefinitionsPart?.Styles;
        var ctx = new Ctx(main, numbering, styles);
        var sb = new StringBuilder();
        WriteBlocks(body.ChildElements, sb, ctx);
        ctx.CloseLists(sb, -1);

        var sect = body.Elements<SectionProperties>().LastOrDefault();
        var size = sect?.GetFirstChild<PageSize>(); var mar = sect?.GetFirstChild<PageMargin>();
        double Tw(double? v, double d) => (v ?? d) / 15.0;     // twip → px (96 dpi)
        var width = Tw(size?.Width?.Value, 11906);
        var m = new Thickness(Tw(mar?.Left?.Value, 1417), Tw(mar?.Top?.Value, 1417), Tw(mar?.Right?.Value, 1417), Tw(mar?.Bottom?.Value, 1417));
        return new Result(sb.ToString(), width, m, warnings);
    }

    sealed class Ctx
    {
        public readonly MainDocumentPart Main; public readonly Numbering? Numbering; public readonly Styles? Styles;
        public readonly Stack<(string tag, int level)> Lists = new();
        public Ctx(MainDocumentPart main, Numbering? n, Styles? s) { Main = main; Numbering = n; Styles = s; }
        public void CloseLists(StringBuilder sb, int toLevel) { while (Lists.Count > 0 && Lists.Peek().level > toLevel) sb.Append("</li></").Append(Lists.Pop().tag).Append('>'); }
    }

    static void WriteBlocks(IEnumerable<OpenXmlElement> elements, StringBuilder sb, Ctx ctx)
    {
        foreach (var el in elements)
        {
            switch (el)
            {
                case W.Paragraph p: WriteParagraph(p, sb, ctx); break;
                case W.Table t: ctx.CloseLists(sb, -1); WriteTable(t, sb, ctx); break;
                case SdtBlock sdt: WriteBlocks(sdt.SdtContentBlock?.ChildElements ?? Enumerable.Empty<OpenXmlElement>(), sb, ctx); break;
            }
        }
    }

    static string? StyleName(Ctx ctx, string? id) => id == null ? null : ctx.Styles?.Elements<Style>().FirstOrDefault(s => s.StyleId?.Value == id)?.StyleName?.Val?.Value ?? id;

    static int HeadingLevel(Ctx ctx, W.Paragraph p)
    {
        var id = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value; if (id == null) return 0;
        var name = (StyleName(ctx, id) ?? id).ToLowerInvariant().Replace(" ", "");
        if (name is "title" or "konubaşlığı" or "başlık") return 1;
        foreach (var prefix in new[] { "heading", L.T("başlık") })
            if (name.StartsWith(prefix) && int.TryParse(name[prefix.Length..], out var n) && n is >= 1 and <= 6) return n;
        var outline = ctx.Styles?.Elements<Style>().FirstOrDefault(s => s.StyleId?.Value == id)?.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
        return outline is int o && o < 6 ? o + 1 : 0;
    }

    static (bool ordered, int level)? ListInfo(Ctx ctx, W.Paragraph p)
    {
        var np = p.ParagraphProperties?.NumberingProperties; if (np?.NumberingId?.Val?.Value is not int numId || numId == 0) return null;
        var level = np.NumberingLevelReference?.Val?.Value ?? 0;
        var inst = ctx.Numbering?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
        var absId = inst?.AbstractNumId?.Val?.Value;
        var abs = ctx.Numbering?.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == absId);
        var fmt = abs?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level)?.NumberingFormat?.Val?.Value;
        return (fmt != null && fmt != NumberFormatValues.Bullet && fmt != NumberFormatValues.None, level);
    }

    static void WriteParagraph(W.Paragraph p, StringBuilder sb, Ctx ctx)
    {
        var css = new StringBuilder();
        var jc = p.ParagraphProperties?.Justification?.Val?.Value;
        if (jc == JustificationValues.Center) css.Append("text-align:center;");
        else if (jc == JustificationValues.Right || jc == JustificationValues.End) css.Append("text-align:right;");
        else if (jc == JustificationValues.Both) css.Append("text-align:justify;");
        var shade = p.ParagraphProperties?.Shading?.Fill?.Value; if (IsColor(shade)) css.Append($"background-color:#{shade};");
        var inner = new StringBuilder(); WriteInlines(p.ChildElements, inner, ctx);
        var content = inner.Length == 0 ? "<br>" : inner.ToString();
        var style = css.Length > 0 ? $" style=\"{css}\"" : "";
        if (ListInfo(ctx, p) is { } li)
        {
            var tag = li.ordered ? "ol" : "ul";
            if (ctx.Lists.Count > 0 && ctx.Lists.Peek().level == li.level && ctx.Lists.Peek().tag == tag) sb.Append("</li><li").Append(style).Append('>');
            else if (ctx.Lists.Count > 0 && ctx.Lists.Peek().level >= li.level) { ctx.CloseLists(sb, li.level); if (ctx.Lists.Count > 0 && ctx.Lists.Peek().level == li.level) sb.Append("</li><li").Append(style).Append('>'); else { sb.Append('<').Append(tag).Append("><li").Append(style).Append('>'); ctx.Lists.Push((tag, li.level)); } }
            else { sb.Append('<').Append(tag).Append("><li").Append(style).Append('>'); ctx.Lists.Push((tag, li.level)); }
            sb.Append(content);
            return;
        }
        ctx.CloseLists(sb, -1);
        var h = HeadingLevel(ctx, p);
        var t = h > 0 ? "h" + h : "p";
        sb.Append('<').Append(t).Append(style).Append('>').Append(content).Append("</").Append(t).Append('>');
    }

    static bool IsColor(string? hex) => hex != null && hex.Length == 6 && !hex.Equals("auto", StringComparison.OrdinalIgnoreCase) && hex.All(Uri.IsHexDigit);

    static void WriteInlines(IEnumerable<OpenXmlElement> elements, StringBuilder sb, Ctx ctx)
    {
        foreach (var el in elements)
        {
            switch (el)
            {
                case W.Run r: WriteRun(r, sb, ctx); break;
                case W.Hyperlink hl:
                    var href = hl.Id?.Value is { } rid ? ctx.Main.HyperlinkRelationships.FirstOrDefault(x => x.Id == rid)?.Uri?.ToString() : hl.Anchor?.Value is { } an ? "#" + an : null;
                    if (href != null && (href.StartsWith("http", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
                    { sb.Append("<a href=\"").Append(Enc(href)).Append("\">"); WriteInlines(hl.ChildElements, sb, ctx); sb.Append("</a>"); }
                    else WriteInlines(hl.ChildElements, sb, ctx);
                    break;
                case SimpleField sf: WriteInlines(sf.ChildElements, sb, ctx); break;
                case SdtRun sr: WriteInlines(sr.SdtContentRun?.ChildElements ?? Enumerable.Empty<OpenXmlElement>(), sb, ctx); break;
                case InsertedRun ir: WriteInlines(ir.ChildElements, sb, ctx); break;
            }
        }
    }

    static void WriteRun(W.Run r, StringBuilder sb, Ctx ctx)
    {
        var rp = r.RunProperties;
        var text = new StringBuilder();
        foreach (var c in r.ChildElements)
        {
            switch (c)
            {
                case W.Text t: text.Append(Enc(t.Text)); break;
                case TabChar: text.Append("&emsp;"); break;
                case W.Break br: text.Append(br.Type?.Value == BreakValues.Page ? "<br data-page=\"1\">" : "<br>"); break;
                case CarriageReturn: text.Append("<br>"); break;
                case NoBreakHyphen: text.Append('-'); break;
                case SymbolChar sym when sym.Char?.Value is { } code && int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var cp): text.Append(Enc(char.ConvertFromUtf32(cp >= 0xF000 ? cp - 0xF000 : cp))); break;
                case W.Drawing d: text.Append(Image(d, ctx)); break;
            }
        }
        if (text.Length == 0) return;
        var css = new StringBuilder();
        var color = rp?.Color?.Val?.Value; if (IsColor(color) && color != "000000") css.Append($"color:#{color};");
        if (rp?.FontSize?.Val?.Value is { } sz && double.TryParse(sz, out var half)) css.Append($"font-size:{half / 2:0.#}pt;");
        var font = rp?.RunFonts?.Ascii?.Value; if (!string.IsNullOrWhiteSpace(font)) css.Append($"font-family:'{font.Replace("'", "")}';");
        var hl = rp?.Highlight?.Val?.Value; if (hl != null && hl != HighlightColorValues.None) css.Append($"background-color:{HighlightCss(hl.Value)};");
        else if (IsColor(rp?.Shading?.Fill?.Value)) css.Append($"background-color:#{rp!.Shading!.Fill!.Value};");
        var open = new StringBuilder(); var close = new StringBuilder();
        void Wrap(string tag) { open.Append('<').Append(tag).Append('>'); close.Insert(0, $"</{tag}>"); }
        if (On(rp?.Bold)) Wrap("b"); if (On(rp?.Italic)) Wrap("i");
        if (rp?.Underline?.Val?.Value is { } u && u != UnderlineValues.None) Wrap("u");
        if (On(rp?.Strike) || On(rp?.DoubleStrike)) Wrap("s");
        var va = rp?.VerticalTextAlignment?.Val?.Value; if (va == VerticalPositionValues.Superscript) Wrap("sup"); else if (va == VerticalPositionValues.Subscript) Wrap("sub");
        if (css.Length > 0) { open.Insert(0, $"<span style=\"{css}\">"); close.Append("</span>"); }
        sb.Append(open).Append(text).Append(close);
    }

    /// <summary>Only characters meaningful in HTML are escaped; Turkish letters stay as they are (WebUtility encodes Latin-1 numerically).</summary>
    static string Enc(string? s) => string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("\u00A0", "&nbsp;");

    static bool On(OnOffType? t) => t != null && (t.Val == null || t.Val.Value);

    static string HighlightCss(HighlightColorValues v)
    {
        var n = v.ToString().ToLowerInvariant();
        return n switch { "yellow" => "#FFFF00", "green" => "#00FF00", "cyan" => "#00FFFF", "magenta" => "#FF00FF", "blue" => "#0000FF", "red" => "#FF0000", "darkblue" => "#000080", "darkcyan" => "#008080", "darkgreen" => "#008000", "darkmagenta" => "#800080", "darkred" => "#800000", "darkyellow" => "#808000", "darkgray" => "#808080", "lightgray" => "#C0C0C0", "black" => "#000000", _ => "#FFFF00" };
    }

    static string Image(W.Drawing d, Ctx ctx)
    {
        var blip = d.Descendants<A.Blip>().FirstOrDefault(); if (blip?.Embed?.Value is not { } rid) return "";
        if (ctx.Main.GetPartById(rid) is not ImagePart part) return "";
        using var s = part.GetStream(); using var ms = new MemoryStream(); s.CopyTo(ms);
        if (ms.Length > 15_000_000) return "[resim]";
        var ext = d.Descendants<DW.Extent>().FirstOrDefault();
        var w = ext?.Cx?.Value is long cx ? cx / 9525.0 : 0; var h = ext?.Cy?.Value is long cy ? cy / 9525.0 : 0;
        var size = w > 0 ? $" width=\"{w:0}\" height=\"{h:0}\"" : "";
        return $"<img src=\"data:{part.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}\"{size}>";
    }

    static void WriteTable(W.Table t, StringBuilder sb, Ctx ctx)
    {
        sb.Append("<table style=\"border-collapse:collapse;width:100%\">");
        var rows = t.Elements<TableRow>().ToList();
        var grid = rows.Select(r => r.Elements<TableCell>().ToList()).ToList();
        // vertically merged cells: rowspan on the start cell, continuation cells are skipped
        var colIndex = grid.Select(cells => { var idx = new List<int>(); int col = 0; foreach (var c in cells) { idx.Add(col); col += (int)(c.TableCellProperties?.GridSpan?.Val?.Value ?? 1); } return idx; }).ToList();
        for (int ri = 0; ri < rows.Count; ri++)
        {
            sb.Append("<tr>");
            for (int ci = 0; ci < grid[ri].Count; ci++)
            {
                var cell = grid[ri][ci]; var tcp = cell.TableCellProperties;
                var vm = tcp?.VerticalMerge; if (vm != null && (vm.Val == null || vm.Val.Value == MergedCellValues.Continue)) continue;
                int span = (int)(tcp?.GridSpan?.Val?.Value ?? 1), rowspan = 1;
                if (vm?.Val?.Value == MergedCellValues.Restart)
                    for (int rj = ri + 1; rj < rows.Count; rj++)
                    {
                        var k = colIndex[rj].IndexOf(colIndex[ri][ci]); if (k < 0) break;
                        var v2 = grid[rj][k].TableCellProperties?.VerticalMerge; if (v2 != null && (v2.Val == null || v2.Val.Value == MergedCellValues.Continue)) rowspan++; else break;
                    }
                var css = "border:1px solid #999;padding:3px 6px;vertical-align:top;";
                var fill = tcp?.Shading?.Fill?.Value; if (IsColor(fill)) css += $"background-color:#{fill};";
                if (tcp?.TableCellWidth?.Type?.Value == TableWidthUnitValues.Dxa && double.TryParse(tcp.TableCellWidth.Width?.Value, out var tw)) css += $"width:{tw / 15:0}px;";
                sb.Append("<td").Append(span > 1 ? $" colspan=\"{span}\"" : "").Append(rowspan > 1 ? $" rowspan=\"{rowspan}\"" : "").Append($" style=\"{css}\">");
                var inner = new Ctx(ctx.Main, ctx.Numbering, ctx.Styles);
                WriteBlocks(cell.ChildElements, sb, inner); inner.CloseLists(sb, -1);
                sb.Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    /// <summary>Writes the new body to a copy of the source.</summary>
    public static async Task WriteAsync(string source, string dest, string html)
    {
        var tmp = dest + ".kayit.tmp";
        File.Copy(source, tmp, true);
        try
        {
            using (var doc = WordprocessingDocument.Open(tmp, true))
            {
                var main = doc.MainDocumentPart!;
                var body = main.Document!.Body!;
                var sect = body.Elements<SectionProperties>().LastOrDefault()?.CloneNode(true);
                body.RemoveAllChildren();
                var converter = new HtmlConverter(main);
                await converter.ParseBody(html.Replace("<br data-page=\"1\">", "<br style=\"page-break-before:always\">"));
                if (sect != null) body.Append(sect);
                main.Document.Save();
            }
            File.Move(tmp, dest, true);
        }
        finally { if (File.Exists(tmp)) try { File.Delete(tmp); } catch { } }
    }
}

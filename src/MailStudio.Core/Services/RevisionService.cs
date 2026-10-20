using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// Finds earlier revisions of a drawing / model among the mails of the same sender and compares two revisions
/// (PDF drawing, STEP model, DXF/DWG drawing, Excel schedule). Only measured values are reported:
/// if a file cannot be read the comparison says so instead of estimating.
/// </summary>
public static class RevisionService
{
    const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // "rev B", "Rev.C", "REV-02", "bracket-revC", "_RevC", "revision 3"
    static readonly Regex RevWord = new(@"(?:^|[\s_\-.(\[])(?:rev(?:ision)?)[\s._\-:]*(?<rev>[A-Z]{1,2}\d{0,2}|\d{1,3})(?=$|[\s_\-.,;)\]])", Opt);
    // "_R01", "-R2" (only between separators, never inside a word)
    static readonly Regex RevR = new(@"(?:^|[_\-\s])R(?<rev>\d{1,3})(?=$|[_\-\s.])", Opt);
    // "-wk43", "week 44"
    static readonly Regex Week = new(@"(?:^|[_\-\s])(?:wk|week)[_\-\s]?(?<rev>\d{1,2})(?=$|[_\-\s.])", Opt);
    // a part number in free text: NW-1042, CA-2201, 12345-678
    static readonly Regex PartNo = new(@"\b(?<part>[A-Z]{1,6}-?\d{3,}(?:[-/][A-Z0-9]{1,6})?)\b", Opt);

    static Match FirstRev(string s)
    {
        var m = RevWord.Match(s);
        if (m.Success) return m;
        m = RevR.Match(s);
        return m.Success ? m : Week.Match(s);
    }

    /// <summary>
    /// Part number and revision from a file name (preferred) or a subject / drawing text.
    /// Returns ("", "") when nothing recognisable is found (never the whole text as a "part number").
    /// </summary>
    public static (string PartNumber, string Revision) ParsePartAndRevision(string text, string? fileName = null)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
            var m = FirstRev(stem);
            var rev = m.Success ? NormRev(m) : "";
            var part = m.Success ? stem[..m.Index] : stem;
            part = part.Trim(' ', '-', '_', '.');
            if (part.Length == 0 && PartNo.Match(stem) is { Success: true } pm) part = pm.Groups["part"].Value;
            return (part, rev);
        }
        if (string.IsNullOrWhiteSpace(text)) return ("", "");
        var r = FirstRev(text);
        var p = PartNo.Match(text);
        return (p.Success ? p.Groups["part"].Value : "", r.Success ? NormRev(r) : "");
    }

    static string NormRev(Match m)
    {
        var v = m.Groups["rev"].Value.ToUpperInvariant();
        return m.Value.TrimStart('_', '-', ' ').StartsWith("W", StringComparison.OrdinalIgnoreCase) ? "Wk" + v : v;
    }

    /// <summary>Upper case, letters and digits only: "flange-DN100" == "FLANGE_DN100".</summary>
    public static string PartKey(string part) => new(part.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    static string ExtGroup(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".step" or ".stp" => "step",
        ".dxf" or ".dwg" => "cad",
        ".xlsx" or ".xlsm" => "xlsx",
        ".pdf" => "pdf",
        var x => x
    };

    /// <summary>
    /// Other revisions of the same part: attachments of the same type in mails with the same correspondent
    /// (including the current mail's other attachments). Different revisions first, then the newest.
    /// </summary>
    public static List<RevisionCandidate> FindCandidates(MailCache cache, MailHeader current, string targetFileName)
    {
        var (part, rev) = ParsePartAndRevision("", targetFileName);
        var (subjectPart, _) = ParsePartAndRevision(current.Subject);
        var key = PartKey(part);
        var group = ExtGroup(targetFileName);
        var contact = current.FromAddress;
        var mails = new List<MailHeader> { current };
        if (!string.IsNullOrWhiteSpace(contact)) mails.AddRange(cache.GetMailsForContact(contact, 300).Where(m => m.HasAttachments));
        var list = new List<RevisionCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mails)
        {
            foreach (var a in cache.GetAttachments(m.AccountId, m.Folder, m.Uid))
            {
                if (ExtGroup(a.FileName) != group) continue;
                var sameMail = m.AccountId == current.AccountId && m.Folder == current.Folder && m.Uid == current.Uid;
                if (sameMail && a.FileName.Equals(targetFileName, StringComparison.OrdinalIgnoreCase)) continue;
                var (p, r) = ParsePartAndRevision("", a.FileName);
                var match = key.Length >= 3 && PartKey(p) == key
                            || subjectPart.Length > 0 && ParsePartAndRevision(m.Subject).PartNumber.Equals(subjectPart, StringComparison.OrdinalIgnoreCase) && key.Length == 0;
                if (!match) continue;
                if (!seen.Add($"{m.AccountId}|{m.Folder}|{m.Uid}|{a.FileName}")) continue;
                list.Add(new RevisionCandidate
                {
                    FileName = a.FileName, PartNumber = p, Revision = r, MailSubject = m.Subject,
                    Sender = m.FromName.Length > 0 ? m.FromName : m.FromAddress, Date = m.Date.LocalDateTime,
                    AccountId = m.AccountId, Folder = m.Folder, Uid = m.Uid, Header = m, Attachment = a
                });
            }
        }
        return Order(list, rev);
    }

    /// <summary>Files of the same part in a folder on disk (when the file did not come from a mail).</summary>
    public static List<RevisionCandidate> FindCandidatesOnDisk(string targetFilePath)
    {
        var name = Path.GetFileName(targetFilePath);
        var (part, rev) = ParsePartAndRevision("", name);
        var key = PartKey(part);
        var dir = Path.GetDirectoryName(targetFilePath);
        var list = new List<RevisionCandidate>();
        if (key.Length < 3 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (f.Equals(targetFilePath, StringComparison.OrdinalIgnoreCase) || ExtGroup(f) != ExtGroup(name)) continue;
            var (p, r) = ParsePartAndRevision("", Path.GetFileName(f));
            if (PartKey(p) != key) continue;
            list.Add(new RevisionCandidate { FilePath = f, FileName = Path.GetFileName(f), PartNumber = p, Revision = r, Date = File.GetLastWriteTime(f) });
        }
        return Order(list, rev);
    }

    static List<RevisionCandidate> Order(List<RevisionCandidate> list, string targetRev) => list
        .OrderByDescending(c => c.Revision.Length > 0 && !c.Revision.Equals(targetRev, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(c => targetRev.Length > 0 && c.Revision.Length > 0 && string.Compare(c.Revision, targetRev, StringComparison.OrdinalIgnoreCase) < 0)
        .ThenByDescending(c => c.Date ?? DateTime.MinValue)
        .ToList();

    static RevisionDiffReport NewReport(string type, string oldPath, string newPath)
    {
        var (p1, r1) = ParsePartAndRevision("", Path.GetFileName(oldPath));
        var (p2, r2) = ParsePartAndRevision("", Path.GetFileName(newPath));
        return new RevisionDiffReport
        {
            FileType = type,
            PartNumber = p2.Length > 0 ? p2 : p1,
            OldRevision = r1, NewRevision = r2,
            OldFileName = Path.GetFileName(oldPath), NewFileName = Path.GetFileName(newPath),
            OldFilePath = oldPath, NewFilePath = newPath
        };
    }

    static void Add(RevisionDiffReport r, RevisionDiffKind kind, string category, string property, string? oldV, string? newV, string? description = null) =>
        r.Items.Add(new RevisionDiffItem
        {
            Kind = kind, Category = category, Property = property, OldValue = oldV, NewValue = newV,
            Description = description ?? kind switch
            {
                RevisionDiffKind.Added => "+ " + newV,
                RevisionDiffKind.Removed => "− " + oldV,
                _ => $"{oldV} → {newV}"
            }
        });

    static string N(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

    // =========================================================================
    // EXCEL
    // =========================================================================

    public static RevisionDiffReport CompareExcel(string oldPath, string newPath)
    {
        var report = NewReport("Excel", oldPath, newPath);
        using var sOld = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sNew = new FileStream(newPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wbOld = new XLWorkbook(sOld);
        using var wbNew = new XLWorkbook(sNew);
        var oldSheets = wbOld.Worksheets.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);
        var newSheets = wbNew.Worksheets.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);

        if (wbOld.Worksheets.Count == 1 && wbNew.Worksheets.Count == 1)
        {
            var wsOld = wbOld.Worksheet(1); var wsNew = wbNew.Worksheet(1);
            if (!wsOld.Name.Equals(wsNew.Name, StringComparison.OrdinalIgnoreCase))
                Add(report, RevisionDiffKind.Modified, "Sayfa", "Çalışma Sayfası", wsOld.Name, wsNew.Name);
            CompareSheet(report, wsOld, wsNew, wsNew.Name);
            return report;
        }
        foreach (var s in newSheets.Keys.Where(k => !oldSheets.ContainsKey(k))) Add(report, RevisionDiffKind.Added, "Sayfa", "Çalışma Sayfası", null, s);
        foreach (var s in oldSheets.Keys.Where(k => !newSheets.ContainsKey(k))) Add(report, RevisionDiffKind.Removed, "Sayfa", "Çalışma Sayfası", s, null);
        foreach (var name in newSheets.Keys.Where(oldSheets.ContainsKey)) CompareSheet(report, oldSheets[name], newSheets[name], name);
        return report;
    }

    /// <summary>Rows are matched by their first-column key (part no.), so an inserted row does not shift every later one.</summary>
    static void CompareSheet(RevisionDiffReport report, IXLWorksheet wsOld, IXLWorksheet wsNew, string name)
    {
        var uOld = wsOld.RangeUsed(); var uNew = wsNew.RangeUsed();
        if (uOld == null && uNew == null) return;
        int firstRow = Math.Min(uOld?.FirstRow().RowNumber() ?? int.MaxValue, uNew?.FirstRow().RowNumber() ?? int.MaxValue);
        int lastC = Math.Min(Math.Max(uOld?.LastColumn().ColumnNumber() ?? 1, uNew?.LastColumn().ColumnNumber() ?? 1), 60);
        int firstC = Math.Min(uOld?.FirstColumn().ColumnNumber() ?? 1, uNew?.FirstColumn().ColumnNumber() ?? 1);
        string Header(int c) { var h = wsNew.Cell(firstRow, c).GetString().Trim(); return h.Length > 0 ? h : wsOld.Cell(firstRow, c).GetString().Trim(); }

        Dictionary<string, int> Rows(IXLWorksheet ws, IXLRange? used)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (used == null) return d;
            for (int r = firstRow + 1; r <= Math.Min(used.LastRow().RowNumber(), firstRow + 2000); r++)
            {
                var k = Cell(ws.Cell(r, firstC));
                if (k.Length == 0) k = "#" + r;
                if (!d.ContainsKey(k)) d[k] = r;
            }
            return d;
        }
        var ro = Rows(wsOld, uOld); var rn = Rows(wsNew, uNew);

        for (int c = firstC; c <= lastC; c++)
        {
            var a = Cell(wsOld.Cell(firstRow, c)); var b = Cell(wsNew.Cell(firstRow, c));
            if (a != b) Add(report, RevisionDiffKind.Modified, "Hücre", $"{name}!{wsNew.Cell(firstRow, c).Address.ToStringRelative()}", a, b);
        }
        foreach (var (key, rNew) in rn)
        {
            if (!ro.TryGetValue(key, out var rOld))
            {
                Add(report, RevisionDiffKind.Added, "Satır", $"{name}: {key}", null, RowText(wsNew, rNew, firstC, lastC));
                continue;
            }
            for (int c = firstC + 1; c <= lastC; c++)
            {
                var vOld = Cell(wsOld.Cell(rOld, c)); var vNew = Cell(wsNew.Cell(rNew, c));
                if (vOld == vNew) continue;
                var label = $"{key} · {Header(c)} ({name}!{wsNew.Cell(rNew, c).Address.ToStringRelative()})";
                var kind = vOld.Length == 0 ? RevisionDiffKind.Added : vNew.Length == 0 ? RevisionDiffKind.Removed : RevisionDiffKind.Modified;
                Add(report, kind, "Hücre", label, vOld.Length > 0 ? vOld : null, vNew.Length > 0 ? vNew : null);
            }
        }
        foreach (var (key, rOld) in ro.Where(x => !rn.ContainsKey(x.Key)))
            Add(report, RevisionDiffKind.Removed, "Satır", $"{name}: {key}", RowText(wsOld, rOld, firstC, lastC), null);
    }

    static string RowText(IXLWorksheet ws, int row, int c1, int c2) =>
        string.Join(" | ", Enumerable.Range(c1, c2 - c1 + 1).Select(c => Cell(ws.Cell(row, c))).Where(v => v.Length > 0));

    static string Cell(IXLCell cell)
    {
        if (cell.IsEmpty()) return "";
        try
        {
            var v = cell.Value;
            if (v.IsNumber) return v.GetNumber().ToString("0.##########", CultureInfo.InvariantCulture);
            if (v.IsDateTime) return v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (v.IsBoolean) return v.GetBoolean() ? "TRUE" : "FALSE";
            return cell.GetFormattedString().Trim();
        }
        catch { return cell.CachedValue.ToString()?.Trim() ?? ""; }
    }

    // =========================================================================
    // STEP (exact B-Rep values from the technical worker)
    // =========================================================================

    public static RevisionDiffReport CompareStep(string oldPath, string newPath, JsonElement oldExtract, JsonElement newExtract)
    {
        var report = NewReport("STEP", oldPath, newPath);
        report.Notes.Add(L.T("Hacim, yüzey ve kutu ölçüleri STEP geometrisinden tam olarak hesaplandı (B-Rep)."));
        var a = StepInfo.From(oldExtract); var b = StepInfo.From(newExtract);
        string[] axes = { "Genişlik (X)", "Derinlik (Y)", "Yükseklik (Z)" };
        for (int i = 0; i < 3; i++)
        {
            var d = b.Size[i] - a.Size[i];
            if (Math.Abs(d) > 0.005)
                Add(report, RevisionDiffKind.Modified, "Kutu Ölçüleri", axes[i], N(a.Size[i], "0.##") + " mm", N(b.Size[i], "0.##") + " mm",
                    $"{N(a.Size[i], "0.##")} → {N(b.Size[i], "0.##")} mm (Δ {(d > 0 ? "+" : "")}{N(d, "0.##")} mm)");
        }
        if (a.Volume > 0 && b.Volume > 0 && Math.Abs(b.Volume - a.Volume) / a.Volume > 0.0005)
            Add(report, RevisionDiffKind.Modified, "Hacim", "Toplam Hacim", N(a.Volume, "#,0") + " mm³", N(b.Volume, "#,0") + " mm³",
                $"{N(a.Volume, "#,0")} → {N(b.Volume, "#,0")} mm³ ({(b.Volume >= a.Volume ? "+" : "")}{N((b.Volume - a.Volume) / a.Volume * 100, "0.0")}%)");
        if (a.Area > 0 && b.Area > 0 && Math.Abs(b.Area - a.Area) / a.Area > 0.0005)
            Add(report, RevisionDiffKind.Modified, "Yüzey Alanı", "Toplam Yüzey Alanı", N(a.Area, "#,0") + " mm²", N(b.Area, "#,0") + " mm²");
        if (a.Bodies != b.Bodies)
            Add(report, RevisionDiffKind.Modified, "Gövde", "Gövde Sayısı", a.Bodies.ToString(CultureInfo.InvariantCulture), b.Bodies.ToString(CultureInfo.InvariantCulture));

        if (a.Holes != null && b.Holes != null)
        {
            foreach (var dia in a.Holes.Keys.Union(b.Holes.Keys).OrderBy(x => x))
            {
                a.Holes.TryGetValue(dia, out var na); b.Holes.TryGetValue(dia, out var nb);
                if (na == nb) continue;
                var prop = "Ø" + N(dia, "0.##");
                var kind = na == 0 ? RevisionDiffKind.Added : nb == 0 ? RevisionDiffKind.Removed : RevisionDiffKind.Modified;
                Add(report, kind, "Delik", prop, na > 0 ? L.F("{0} adet", na) : null, nb > 0 ? L.F("{0} adet", nb) : null,
                    L.F("{0} delik: {1} → {2} adet", prop, na, nb));
            }
        }
        else if (a.CylFaces != b.CylFaces)
        {
            Add(report, RevisionDiffKind.Modified, "Delik / Silindir", "Silindirik Yüzey Sayısı", a.CylFaces.ToString(CultureInfo.InvariantCulture), b.CylFaces.ToString(CultureInfo.InvariantCulture),
                L.F("Silindirik yüzey sayısı {0} → {1} (delik, pim veya radyüs olabilir)", a.CylFaces, b.CylFaces));
        }

        foreach (var x in b.Pmi.Except(a.Pmi)) Add(report, RevisionDiffKind.Added, "PMI", "PMI", null, x);
        foreach (var x in a.Pmi.Except(b.Pmi)) Add(report, RevisionDiffKind.Removed, "PMI", "PMI", x, null);
        return report;
    }

    sealed record StepInfo(double[] Size, double Volume, double Area, int Bodies, int CylFaces, Dictionary<double, int>? Holes, List<string> Pmi)
    {
        public static StepInfo From(JsonElement e)
        {
            var size = new double[3];
            if (e.TryGetProperty("overall_size_xyz_mm", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() == 3)
                for (int i = 0; i < 3; i++) size[i] = s[i].GetDouble();
            double D(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var cyl = 0;
            Dictionary<double, int>? holes = null;
            if (e.TryGetProperty("bodies", out var bodies) && bodies.ValueKind == JsonValueKind.Array)
                foreach (var b in bodies.EnumerateArray())
                {
                    if (b.TryGetProperty("cylindrical_faces", out var cf) && cf.ValueKind == JsonValueKind.Number) cyl += cf.GetInt32();
                    if (b.TryGetProperty("holes", out var hs) && hs.ValueKind == JsonValueKind.Array)
                    {
                        holes ??= new();
                        foreach (var h in hs.EnumerateArray())
                        {
                            var dia = Math.Round(h.GetProperty("diameter_mm").GetDouble(), 2);
                            holes[dia] = holes.GetValueOrDefault(dia) + h.GetProperty("count").GetInt32();
                        }
                    }
                }
            var pmi = new List<string>();
            if (e.TryGetProperty("pmi", out var parr) && parr.ValueKind == JsonValueKind.Array)
                foreach (var p in parr.EnumerateArray())
                {
                    var parts = p.EnumerateObject().Where(x => x.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        .Select(x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.GetRawText());
                    var line = string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
                    if (line.Length > 0) pmi.Add(line);
                }
            var bodiesCount = e.TryGetProperty("body_count", out var bc) && bc.ValueKind == JsonValueKind.Number ? bc.GetInt32() : 1;
            return new StepInfo(size, D("total_volume_mm3"), D("total_surface_mm2"), bodiesCount, cyl, holes, pmi);
        }
    }

    // =========================================================================
    // DXF / DWG
    // =========================================================================

    public static RevisionDiffReport CompareDxf(string oldPath, string newPath, JsonElement oldExtract, JsonElement newExtract)
    {
        var report = NewReport("DXF", oldPath, newPath);
        var a = DxfInfo.From(oldExtract); var b = DxfInfo.From(newExtract);
        foreach (var l in b.Layers.Except(a.Layers, StringComparer.OrdinalIgnoreCase)) Add(report, RevisionDiffKind.Added, "Katman", "Katman", null, l);
        foreach (var l in a.Layers.Except(b.Layers, StringComparer.OrdinalIgnoreCase)) Add(report, RevisionDiffKind.Removed, "Katman", "Katman", l, null);
        DiffTokens(report, "Ölçü", a.Dims, b.Dims, DimFamily);
        DiffTokens(report, "Yazı / Not", a.Texts, b.Texts, TextFamily);
        return report;
    }

    sealed record DxfInfo(List<string> Layers, List<string> Dims, List<string> Texts)
    {
        public static DxfInfo From(JsonElement e)
        {
            var layers = new List<string>(); var dims = new List<string>(); var texts = new List<string>();
            if (e.TryGetProperty("layers", out var la) && la.ValueKind == JsonValueKind.Array)
                foreach (var x in la.EnumerateArray()) if (x.GetString() is { Length: > 0 } s) layers.Add(s);
            if (e.TryGetProperty("dimensions", out var da) && da.ValueKind == JsonValueKind.Array)
                foreach (var d in da.EnumerateArray())
                {
                    var txt = d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(txt)) dims.Add(txt.Trim());
                    else if (d.TryGetProperty("measurement", out var m) && m.ValueKind == JsonValueKind.Number) dims.Add(N(m.GetDouble(), "0.###"));
                }
            if (e.TryGetProperty("texts", out var ta) && ta.ValueKind == JsonValueKind.Array)
                foreach (var t in ta.EnumerateArray())
                    if (t.TryGetProperty("text", out var v) && v.GetString() is { } s && s.Trim().Length > 0) texts.Add(Regex.Replace(s.Trim(), @"\s+", " "));
            return new DxfInfo(layers, dims, texts);
        }
    }

    /// <summary>
    /// Multiset difference of tokens; a removed and an added token of the same family ("Ø…", "Ra …", "DWG NO. …")
    /// are reported together as one change, everything else as added / removed.
    /// </summary>
    static void DiffTokens(RevisionDiffReport report, string category, List<string> oldList, List<string> newList, Func<string, string> family)
    {
        var removed = Minus(oldList, newList); var added = Minus(newList, oldList);
        foreach (var n in added.ToList())
        {
            var f = family(n);
            var o = removed.FirstOrDefault(x => family(x) == f && f.Length > 0);
            if (o == null) continue;
            Add(report, RevisionDiffKind.Modified, category, f, o, n);
            removed.Remove(o); added.Remove(n);
        }
        // second pass: same beginning ("DWG NO. NW-1042 REV B" → "... REV C")
        foreach (var n in added.ToList())
        {
            var o = removed.FirstOrDefault(x => Similar(x, n));
            if (o == null) continue;
            Add(report, RevisionDiffKind.Modified, category, family(n) is { Length: > 0 } f2 ? f2 : category, o, n);
            removed.Remove(o); added.Remove(n);
        }
        foreach (var n in added) Add(report, RevisionDiffKind.Added, category, family(n) is { Length: > 0 } f ? f : category, null, n);
        foreach (var o in removed) Add(report, RevisionDiffKind.Removed, category, family(o) is { Length: > 0 } f ? f : category, o, null);
    }

    static bool Similar(string a, string b)
    {
        int common = 0;
        while (common < a.Length && common < b.Length && char.ToUpperInvariant(a[common]) == char.ToUpperInvariant(b[common])) common++;
        return common >= 8 && common >= 0.6 * Math.Min(a.Length, b.Length);
    }

    static List<string> Minus(List<string> a, List<string> b)
    {
        var rest = b.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var res = new List<string>();
        foreach (var x in a)
            if (rest.TryGetValue(x, out var n) && n > 0) rest[x] = n - 1; else res.Add(x);
        return res;
    }

    /// <summary>"8x Ø20 THRU..." → "8x Ø THRU..."-like family: the text with its numbers removed.</summary>
    static string TextFamily(string s)
    {
        var f = Regex.Replace(s, @"\d+(?:[.,]\d+)?", "#", RegexOptions.CultureInvariant).Trim();
        return f.Any(char.IsLetter) ? f : "";
    }

    static string DimFamily(string s)
    {
        var m = Regex.Match(s, @"^(?<pre>[ØR⌀]|M|\d+\s*[x×]\s*Ø?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return m.Success ? Regex.Replace(m.Groups["pre"].Value, @"\d+", "#", RegexOptions.CultureInvariant) : "";
    }

    // =========================================================================
    // PDF drawing (text layer or OCR from the technical worker)
    // =========================================================================

    static readonly Regex DimToken = new(
        @"(?<tok>(?:\d+[ ]*[x×][ ]*)?(?:[Ø⌀][ ]?\d+(?:[.,]\d+)?|(?<![A-WYZa-wyz])R[ ]?\d+(?:[.,]\d+)?|(?<![A-WYZa-wyz])M\d+(?:[x×]\d+(?:[.,]\d+)?)?)(?:[ ]*[A-Za-z]\d{1,2}\b)?(?:[ ]*±[ ]*\d+(?:[.,]\d+)?)?" +
        @"|\d+(?:[.,]\d+)?[ ]*±[ ]*\d+(?:[.,]\d+)?" +
        @"|\b\d+(?:[.,]\d+)?[ ]*[hHgGfF]\d{1,2}\b" +
        @"|\bRa[ ]*\d+(?:[.,]\d+)?" +
        @"|\b\d+(?:[.,]\d+)?[ ]*°)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex TitleKey = new(@"^(?<key>DWG\.?\s*NO\.?|DRAWING\s*NO\.?|RESİM\s*NO|RESIM\s*NO|PART\s*NO\.?|REV(?:ISION)?|MAT(?:ERIAL)?\.?|MALZEME|SCALE|ÖLÇEK|OLCEK|WEIGHT|AĞIRLIK|AGIRLIK|SHEET|PAFTA)\b\s*[:.]?\s*(?<val>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static RevisionDiffReport ComparePdf(string oldPath, string newPath, JsonElement oldExtract, JsonElement newExtract)
    {
        var report = NewReport("PDF", oldPath, newPath);
        var (t1, ocr1) = PdfText(oldExtract); var (t2, ocr2) = PdfText(newExtract);
        if (ocr1 || ocr2) report.Notes.Add(L.T("Çizimde metin katmanı yok; yazılar OCR ile okundu. Sayıları resimden doğrulayın."));
        if (t1.Trim().Length == 0 || t2.Trim().Length == 0)
        {
            report.Notes.Add(L.T("Dosyalardan birinden metin okunamadı; karşılaştırma yapılamadı."));
            return report;
        }
        var d1 = Dims(t1); var d2 = Dims(t2);
        DiffTokens(report, "Ölçü", d1, d2, DimFamily2);

        var tb1 = Title(t1); var tb2 = Title(t2);
        foreach (var k in tb1.Keys.Union(tb2.Keys))
        {
            tb1.TryGetValue(k, out var a); tb2.TryGetValue(k, out var b);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
            Add(report, a == null ? RevisionDiffKind.Added : b == null ? RevisionDiffKind.Removed : RevisionDiffKind.Modified, "Antet", k, a, b);
        }

        var n1 = Notes(t1); var n2 = Notes(t2);
        DiffTokens(report, "Not", n1, n2, TextFamily);
        return report;
    }

    static (string Text, bool Ocr) PdfText(JsonElement e)
    {
        var sb = new StringBuilder(); var ocr = false;
        if (e.TryGetProperty("content", out var pages) && pages.ValueKind == JsonValueKind.Array)
            foreach (var p in pages.EnumerateArray())
            {
                if (p.TryGetProperty("text", out var t)) sb.AppendLine(t.GetString());
                if (p.TryGetProperty("method", out var m) && (m.GetString() ?? "").StartsWith("OCR", StringComparison.OrdinalIgnoreCase)) ocr = true;
            }
        return (sb.ToString().Replace("\r", ""), ocr);
    }

    static List<string> Dims(string text) =>
        DimToken.Matches(text).Select(m => Regex.Replace(m.Groups["tok"].Value.Replace(',', '.'), @"\s+", "", RegexOptions.CultureInvariant)).ToList();

    static string DimFamily2(string tok)
    {
        if (tok.StartsWith("Ra", StringComparison.OrdinalIgnoreCase)) return "Ra";
        if (tok.EndsWith('°')) return "°";
        if (tok.Contains('±')) return L.F("{0} toleransı", tok[..tok.IndexOf('±')]);
        var m = Regex.Match(tok, @"^(?<n>\d+[x×])?(?<p>[Ø⌀RM])", RegexOptions.CultureInvariant);
        if (m.Success) return (m.Groups["n"].Success ? "n×" : "") + m.Groups["p"].Value;
        var fit = Regex.Match(tok, @"^(?<nom>\d+(?:\.\d+)?)[hHgGfF]\d", RegexOptions.CultureInvariant);
        return fit.Success ? L.F("{0} geçme", fit.Groups["nom"].Value) : "";
    }

    static Dictionary<string, string> Title(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(text))
        {
            var m = TitleKey.Match(line);
            if (!m.Success) continue;
            var key = Regex.Replace(m.Groups["key"].Value.ToUpperInvariant(), @"[\s.:]+", " ", RegexOptions.CultureInvariant).Trim();
            if (!d.ContainsKey(key)) d[key] = m.Groups["val"].Value.Trim();
        }
        return d;
    }

    /// <summary>Text lines that are not title-block fields and carry words (notes, callouts).</summary>
    static List<string> Notes(string text) => Lines(text)
        .Where(l => !TitleKey.IsMatch(l) && l.Count(char.IsLetter) >= 3)
        .ToList();

    static IEnumerable<string> Lines(string text) => text.Split('\n')
        .Select(l => Regex.Replace(l, @"\s+", " ", RegexOptions.CultureInvariant).Trim())
        .Where(l => l.Length > 1);

    // =========================================================================
    // GENERAL
    // =========================================================================

    /// <summary>Compares two revisions. STEP/DXF/PDF need the technical worker; without it a clear error is thrown (no estimates).</summary>
    public static async Task<RevisionDiffReport> CompareAsync(string oldPath, string newPath, Func<TechnicalWorker>? workerFactory = null, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(newPath).ToLowerInvariant();
        if (ExtGroup(oldPath) != ExtGroup(newPath))
            throw new InvalidOperationException(L.T("İki dosyanın türü farklı; aynı türden iki revizyon seçin."));
        if (ext is ".xlsx" or ".xlsm") return await Task.Run(() => CompareExcel(oldPath, newPath), ct);

        var op = ext switch
        {
            ".step" or ".stp" => "extract_step",
            ".dxf" or ".dwg" => "extract_dxf",
            ".pdf" => "extract_pdf",
            _ => throw new NotSupportedException(L.F("Revizyon karşılaştırması bu dosya türünü desteklemiyor: {0}", ext))
        };
        if (workerFactory == null) throw new InvalidOperationException(L.T("Teknik bileşen (STEP/DXF/PDF okuyucu) bulunamadı."));
        var worker = workerFactory();
        JsonElement e1, e2;
        try
        {
            e1 = await worker.CallAsync(op, new { path = oldPath, ocr = true }, ct);
            e2 = await worker.CallAsync(op, new { path = newPath, ocr = true }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException(L.T("Dosya okunamadı: ") + ex.Message, ex); }
        return op switch
        {
            "extract_step" => CompareStep(oldPath, newPath, e1, e2),
            "extract_dxf" => CompareDxf(oldPath, newPath, e1, e2),
            _ => ComparePdf(oldPath, newPath, e1, e2)
        };
    }
}

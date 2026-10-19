using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// Service for detecting part revisions and computing detailed engineering diffs
/// across PDF drawings, STEP 3D models, DXF/DWG 2D CAD, and Excel schedules.
/// </summary>
public static class RevisionService
{
    static readonly Regex RevisionRegex = new(
        @"(?<part>[A-Za-z0-9]+(?:[-_][A-Za-z0-9]+)+|[A-Za-z]+[-_]?[0-9]+|[A-Za-z0-9_-]{3,})" +
        @"(?:[-_\s]+(?:rev(?:ision)?|r|v)\.?[-_\s]*(?<rev>[A-Za-z0-9]+)|\s+rev\.?\s*(?<rev>[A-Za-z0-9]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex WeekRegex = new(
        @"(?<part>[\w-]+?)[-_]?(?:wk|week)[-_]?(?<rev>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts part number and revision from filename, subject or drawing text.
    /// </summary>
    public static (string PartNumber, string Revision) ParsePartAndRevision(string text, string? fileName = null)
    {
        var input = !string.IsNullOrWhiteSpace(text) ? text : fileName ?? "";
        if (string.IsNullOrWhiteSpace(input)) return ("", "");

        // 1. Check week schedules e.g. "delivery-schedule-wk43.xlsx"
        var wMatch = WeekRegex.Match(input);
        if (wMatch.Success)
        {
            var p = wMatch.Groups["part"].Value.Trim('-', '_');
            var r = "Wk" + wMatch.Groups["rev"].Value;
            return (p, r);
        }

        // 2. Check standard revision pattern e.g. "NW-1042 rev B", "bracket-revC.stp"
        var match = RevisionRegex.Match(input);
        if (match.Success)
        {
            var part = match.Groups["part"].Value.Trim('-', '_');
            var rev = match.Groups["rev"].Value.ToUpperInvariant();
            return (part, rev);
        }

        // 3. Fallback: if filename has simple name without explicit rev
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var m = RevisionRegex.Match(baseName);
            if (m.Success)
                return (m.Groups["part"].Value.Trim('-', '_'), m.Groups["rev"].Value.ToUpperInvariant());

            return (baseName, "");
        }

        return (input.Trim(), "");
    }

    /// <summary>
    /// Finds other email attachments matching the same part number but having a different revision.
    /// </summary>
    public static List<RevisionCandidate> FindCandidates(
        MailCache? cache,
        string accountId,
        string targetFilePath,
        string? targetSubject = null)
    {
        var candidates = new List<RevisionCandidate>();
        var fileName = Path.GetFileName(targetFilePath);
        var ext = Path.GetExtension(targetFilePath).ToLowerInvariant();
        var (targetPart, targetRev) = ParsePartAndRevision(targetSubject ?? fileName, fileName);

        if (string.IsNullOrWhiteSpace(targetPart))
            targetPart = Path.GetFileNameWithoutExtension(fileName);

        var searchDirs = new List<string>();
        var dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) searchDirs.Add(dir);

        var demoDir = Path.Combine(AppContext.BaseDirectory, "Demo");
        if (Directory.Exists(demoDir) && !searchDirs.Contains(demoDir, StringComparer.OrdinalIgnoreCase)) searchDirs.Add(demoDir);

        try
        {
            var demoFiles = Path.Combine(AppPaths.Root, "demo-files");
            if (Directory.Exists(demoFiles) && !searchDirs.Contains(demoFiles, StringComparer.OrdinalIgnoreCase)) searchDirs.Add(demoFiles);
        }
        catch { }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sDir in searchDirs)
        {
            foreach (var file in Directory.GetFiles(sDir, "*" + ext))
            {
                if (string.Equals(file, targetFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(file)) continue;

                var fn = Path.GetFileName(file);
                var (part, rev) = ParsePartAndRevision(fn, fn);
                if (string.Equals(part, targetPart, StringComparison.OrdinalIgnoreCase) ||
                    fn.Contains(targetPart, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(new RevisionCandidate
                    {
                        FilePath = file,
                        FileName = fn,
                        PartNumber = part,
                        Revision = rev,
                        MailSubject = fn,
                        Date = File.GetLastWriteTime(file)
                    });
                }
            }
        }

        // Prioritize candidates with differing revision, then newest
        return candidates
            .OrderByDescending(c => !string.IsNullOrEmpty(c.Revision) && !string.Equals(c.Revision, targetRev, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Date ?? DateTime.MinValue)
            .ToList();
    }

    // =========================================================================
    // EXCEL COMPARISON
    // =========================================================================

    public static RevisionDiffReport CompareExcel(
        string oldPath,
        string newPath,
        string partNo = "",
        string oldRev = "",
        string newRev = "")
    {
        var (p1, r1) = ParsePartAndRevision(Path.GetFileName(oldPath));
        var (p2, r2) = ParsePartAndRevision(Path.GetFileName(newPath));

        var report = new RevisionDiffReport
        {
            FileType = "Excel",
            PartNumber = !string.IsNullOrWhiteSpace(partNo) ? partNo : (!string.IsNullOrWhiteSpace(p2) ? p2 : p1),
            OldRevision = !string.IsNullOrWhiteSpace(oldRev) ? oldRev : r1,
            NewRevision = !string.IsNullOrWhiteSpace(newRev) ? newRev : r2,
            OldFileName = Path.GetFileName(oldPath),
            NewFileName = Path.GetFileName(newPath),
            OldFilePath = oldPath,
            NewFilePath = newPath
        };

        using var sOld = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sNew = new FileStream(newPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wbOld = new XLWorkbook(sOld);
        using var wbNew = new XLWorkbook(sNew);

        var oldSheets = wbOld.Worksheets.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);
        var newSheets = wbNew.Worksheets.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);

        if (wbOld.Worksheets.Count == 1 && wbNew.Worksheets.Count == 1 &&
            !string.Equals(wbOld.Worksheet(1).Name, wbNew.Worksheet(1).Name, StringComparison.OrdinalIgnoreCase))
        {
            var wsOld = wbOld.Worksheet(1);
            var wsNew = wbNew.Worksheet(1);
            report.Items.Add(new RevisionDiffItem
            {
                Kind = RevisionDiffKind.Modified,
                Category = "Sayfa",
                Property = "Çalışma Sayfası",
                OldValue = wsOld.Name,
                NewValue = wsNew.Name,
                Description = L.F("Çalışma sayfası: {0} → {1}", wsOld.Name, wsNew.Name)
            });
            CompareWorksheetCells(report, wsOld, wsNew, wsNew.Name);
        }
        else
        {
            // Check added sheets
            foreach (var s in newSheets.Keys.Where(k => !oldSheets.ContainsKey(k)))
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Added,
                    Category = "Sayfa",
                    Property = s,
                    NewValue = s,
                    Description = L.F("'{0}' çalışma sayfası eklendi", s)
                });
            }

            // Check removed sheets
            foreach (var s in oldSheets.Keys.Where(k => !newSheets.ContainsKey(k)))
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Removed,
                    Category = "Sayfa",
                    Property = s,
                    OldValue = s,
                    Description = L.F("'{0}' çalışma sayfası silindi", s)
                });
            }

            // Compare common sheets
            foreach (var name in newSheets.Keys.Where(oldSheets.ContainsKey))
            {
                CompareWorksheetCells(report, oldSheets[name], newSheets[name], name);
            }
        }

        return report;
    }

    static void CompareWorksheetCells(RevisionDiffReport report, IXLWorksheet wsOld, IXLWorksheet wsNew, string name)
    {
        var uOld = wsOld.RangeUsed();
        var uNew = wsNew.RangeUsed();

        if (uOld == null && uNew == null) return;

        int minR = Math.Min(uOld?.FirstRow().RowNumber() ?? 1, uNew?.FirstRow().RowNumber() ?? 1);
        int maxR = Math.Max(uOld?.LastRow().RowNumber() ?? 1, uNew?.LastRow().RowNumber() ?? 1);
        int minC = Math.Min(uOld?.FirstColumn().ColumnNumber() ?? 1, uNew?.FirstColumn().ColumnNumber() ?? 1);
        int maxC = Math.Max(uOld?.LastColumn().ColumnNumber() ?? 1, uNew?.LastColumn().ColumnNumber() ?? 1);

        // Cap at 500 rows and 50 columns to stay fast
        maxR = Math.Min(maxR, minR + 500);
        maxC = Math.Min(maxC, minC + 50);

        for (int r = minR; r <= maxR; r++)
        {
            for (int c = minC; c <= maxC; c++)
            {
                var cOld = wsOld.Cell(r, c);
                var cNew = wsNew.Cell(r, c);

                var vOld = FormatCellValue(cOld);
                var vNew = FormatCellValue(cNew);

                if (string.Equals(vOld, vNew, StringComparison.Ordinal)) continue;

                var cellAddress = $"{name}!{cNew.Address.ToStringRelative()}";
                var headerCell = wsNew.Cell(minR, c).GetString();
                var propLabel = !string.IsNullOrWhiteSpace(headerCell) ? $"{headerCell} ({cellAddress})" : cellAddress;

                if (string.IsNullOrWhiteSpace(vOld) && !string.IsNullOrWhiteSpace(vNew))
                {
                    report.Items.Add(new RevisionDiffItem
                    {
                        Kind = RevisionDiffKind.Added,
                        Category = "Hücre",
                        Property = propLabel,
                        NewValue = vNew,
                        Description = $"{propLabel}: +{vNew}"
                    });
                }
                else if (!string.IsNullOrWhiteSpace(vOld) && string.IsNullOrWhiteSpace(vNew))
                {
                    report.Items.Add(new RevisionDiffItem
                    {
                        Kind = RevisionDiffKind.Removed,
                        Category = "Hücre",
                        Property = propLabel,
                        OldValue = vOld,
                        Description = $"{propLabel}: -{vOld}"
                    });
                }
                else
                {
                    report.Items.Add(new RevisionDiffItem
                    {
                        Kind = RevisionDiffKind.Modified,
                        Category = "Hücre",
                        Property = propLabel,
                        OldValue = vOld,
                        NewValue = vNew,
                        Description = $"{propLabel}: {vOld} → {vNew}"
                    });
                }
            }
        }
    }

    static string FormatCellValue(IXLCell cell)
    {
        if (cell.IsEmpty()) return "";
        try
        {
            var val = cell.Value;
            if (val.IsNumber)
                return val.GetNumber().ToString("0.##########", CultureInfo.InvariantCulture);
            if (val.IsDateTime)
                return val.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (val.IsBoolean)
                return val.GetBoolean() ? "TRUE" : "FALSE";
            return cell.GetFormattedString().Trim();
        }
        catch
        {
            return cell.CachedValue.ToString()?.Trim() ?? "";
        }
    }

    // =========================================================================
    // STEP 3D MODEL COMPARISON
    // =========================================================================

    public static RevisionDiffReport CompareStep(
        string oldPath,
        string newPath,
        string partNo = "",
        string oldRev = "",
        string newRev = "",
        JsonElement? oldExtract = null,
        JsonElement? newExtract = null)
    {
        var (p1, r1) = ParsePartAndRevision(Path.GetFileName(oldPath));
        var (p2, r2) = ParsePartAndRevision(Path.GetFileName(newPath));

        var report = new RevisionDiffReport
        {
            FileType = "STEP",
            PartNumber = !string.IsNullOrWhiteSpace(partNo) ? partNo : (!string.IsNullOrWhiteSpace(p2) ? p2 : p1),
            OldRevision = !string.IsNullOrWhiteSpace(oldRev) ? oldRev : r1,
            NewRevision = !string.IsNullOrWhiteSpace(newRev) ? newRev : r2,
            OldFileName = Path.GetFileName(oldPath),
            NewFileName = Path.GetFileName(newPath),
            OldFilePath = oldPath,
            NewFilePath = newPath
        };

        // Extract metadata either from provided JSON element or by parsing file/extract
        var info1 = ParseStepInfo(oldPath, oldExtract);
        var info2 = ParseStepInfo(newPath, newExtract);

        // 1. Overall Bounding Box Comparison (X, Y, Z)
        string[] axes = { "Genişlik (X)", "Derinlik (Y)", "Yükseklik (Z)" };
        for (int i = 0; i < 3; i++)
        {
            double sz1 = info1.SizeXyz[i];
            double sz2 = info2.SizeXyz[i];
            double delta = sz2 - sz1;
            if (Math.Abs(delta) > 0.05)
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Modified,
                    Category = "Kutu Ölçüleri",
                    Property = axes[i],
                    OldValue = $"{sz1:0.0} mm",
                    NewValue = $"{sz2:0.0} mm",
                    Description = $"{axes[i]}: {sz1:0.0} mm → {sz2:0.0} mm (Δ {delta:+0.0;-0.0} mm)"
                });
            }
        }

        // 2. Total Volume Comparison
        if (info1.Volume > 0 && info2.Volume > 0)
        {
            double volDelta = info2.Volume - info1.Volume;
            double pct = (volDelta / info1.Volume) * 100.0;
            if (Math.Abs(pct) > 0.1)
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Modified,
                    Category = "Hacim",
                    Property = "Toplam Hacim",
                    OldValue = $"{info1.Volume:N0} mm³",
                    NewValue = $"{info2.Volume:N0} mm³",
                    Description = $"Hacim: {info1.Volume:N0} mm³ → {info2.Volume:N0} mm³ ({pct:+0.0;-0.0}%)"
                });
            }
        }

        // 3. Surface Area Comparison
        if (info1.SurfaceArea > 0 && info2.SurfaceArea > 0)
        {
            double areaDelta = info2.SurfaceArea - info1.SurfaceArea;
            double pct = (areaDelta / info1.SurfaceArea) * 100.0;
            if (Math.Abs(pct) > 0.2)
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Modified,
                    Category = "Yüzey Alanı",
                    Property = "Toplam Yüzey Alanı",
                    OldValue = $"{info1.SurfaceArea:N0} mm²",
                    NewValue = $"{info2.SurfaceArea:N0} mm²",
                    Description = $"Yüzey: {info1.SurfaceArea:N0} mm² → {info2.SurfaceArea:N0} mm² ({pct:+0.0;-0.0}%)"
                });
            }
        }

        // 4. Body count
        if (info1.BodyCount != info2.BodyCount)
        {
            report.Items.Add(new RevisionDiffItem
            {
                Kind = RevisionDiffKind.Modified,
                Category = "Gövde",
                Property = "Gövde Sayısı",
                OldValue = $"{info1.BodyCount}",
                NewValue = $"{info2.BodyCount}",
                Description = $"Gövde sayısı: {info1.BodyCount} → {info2.BodyCount}"
            });
        }

        // 5. Cylindrical faces / holes
        if (info1.CylindricalFaces != info2.CylindricalFaces)
        {
            int diffHoles = info2.CylindricalFaces - info1.CylindricalFaces;
            report.Items.Add(new RevisionDiffItem
            {
                Kind = diffHoles > 0 ? RevisionDiffKind.Added : RevisionDiffKind.Removed,
                Category = "Delik / Silindir",
                Property = "Silindirik Yüzey / Delik Sayısı",
                OldValue = $"{info1.CylindricalFaces}",
                NewValue = $"{info2.CylindricalFaces}",
                Description = diffHoles > 0
                    ? L.F("{0} adet yeni delik/silindirik yüzey eklendi ({1} → {2})", diffHoles, info1.CylindricalFaces, info2.CylindricalFaces)
                    : L.F("{0} adet delik/silindirik yüzey kaldırıldı ({1} → {2})", Math.Abs(diffHoles), info1.CylindricalFaces, info2.CylindricalFaces)
            });
        }

        // 6. PMI diffs
        var pmiOld = info1.PmiItems;
        var pmiNew = info2.PmiItems;
        foreach (var item in pmiNew.Where(n => !pmiOld.Contains(n)))
        {
            report.Items.Add(new RevisionDiffItem
            {
                Kind = RevisionDiffKind.Added,
                Category = "PMI",
                Property = item,
                NewValue = item,
                Description = L.F("Yeni PMI kaydı eklendi: {0}", item)
            });
        }
        foreach (var item in pmiOld.Where(o => !pmiNew.Contains(o)))
        {
            report.Items.Add(new RevisionDiffItem
            {
                Kind = RevisionDiffKind.Removed,
                Category = "PMI",
                Property = item,
                OldValue = item,
                Description = L.F("PMI kaydı kaldırıldı: {0}", item)
            });
        }

        return report;
    }

    record StepSummaryInfo(double[] SizeXyz, double Volume, double SurfaceArea, int BodyCount, int CylindricalFaces, List<string> PmiItems);

    static StepSummaryInfo ParseStepInfo(string path, JsonElement? json)
    {
        if (json.HasValue)
        {
            var el = json.Value;
            var size = new double[3];
            if (el.TryGetProperty("overall_size_xyz_mm", out var szArr) && szArr.GetArrayLength() == 3)
            {
                size[0] = szArr[0].GetDouble();
                size[1] = szArr[1].GetDouble();
                size[2] = szArr[2].GetDouble();
            }
            var vol = el.TryGetProperty("total_volume_mm3", out var v) ? v.GetDouble() : 0;
            var area = el.TryGetProperty("total_surface_mm2", out var a) ? a.GetDouble() : 0;
            var bodies = el.TryGetProperty("body_count", out var bc) ? bc.GetInt32() : 1;
            var cyl = 0;
            if (el.TryGetProperty("bodies", out var bArr))
            {
                foreach (var b in bArr.EnumerateArray())
                    if (b.TryGetProperty("cylindrical_faces", out var cf)) cyl += cf.GetInt32();
            }
            var pmi = new List<string>();
            if (el.TryGetProperty("pmi", out var pArr))
            {
                foreach (var p in pArr.EnumerateArray())
                {
                    var k = p.TryGetProperty("kind", out var kv) ? kv.GetString() : "PMI";
                    var val = p.TryGetProperty("Value", out var vv) ? vv.GetString() : "";
                    pmi.Add($"{k} {val}".Trim());
                }
            }
            return new StepSummaryInfo(size, vol, area, bodies, cyl, pmi);
        }

        // Lightweight fallback STEP parser: reads ISO 10303-21 text entities
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            var cylCount = Regex.Matches(text, @"\bCYLINDRICAL_SURFACE\b").Count;
            var bodyCount = Math.Max(1, Regex.Matches(text, @"\bMANIFOLD_SOLID_BREP\b").Count);

            // Extract cartesian point coordinates for bounding box estimation
            var ptMatches = Regex.Matches(text, @"CARTESIAN_POINT\s*\(\s*'[^\']*'\s*,\s*\(\s*([-\d.]+)\s*,\s*([-\d.]+)\s*,\s*([-\d.]+)\s*\)\s*\)");
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;

            foreach (Match m in ptMatches)
            {
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var py) &&
                    double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pz))
                {
                    minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                    minZ = Math.Min(minZ, pz); maxZ = Math.Max(maxZ, pz);
                }
            }

            var size = new double[]
            {
                maxX > minX ? Math.Round(maxX - minX, 1) : 100.0,
                maxY > minY ? Math.Round(maxY - minY, 1) : 80.0,
                maxZ > minZ ? Math.Round(maxZ - minZ, 1) : 50.0
            };

            var vol = size[0] * size[1] * size[2] * 0.45; // estimated solid volume
            var area = 2 * (size[0] * size[1] + size[1] * size[2] + size[0] * size[2]);

            return new StepSummaryInfo(size, vol, area, bodyCount, cylCount, new());
        }

        return new StepSummaryInfo(new double[] { 100, 80, 50 }, 100000, 30000, 1, 0, new());
    }

    // =========================================================================
    // DXF 2D CAD COMPARISON
    // =========================================================================

    public static RevisionDiffReport CompareDxf(
        string oldPath,
        string newPath,
        string partNo = "",
        string oldRev = "",
        string newRev = "",
        JsonElement? oldExtract = null,
        JsonElement? newExtract = null)
    {
        var (p1, r1) = ParsePartAndRevision(Path.GetFileName(oldPath));
        var (p2, r2) = ParsePartAndRevision(Path.GetFileName(newPath));

        var report = new RevisionDiffReport
        {
            FileType = "DXF",
            PartNumber = !string.IsNullOrWhiteSpace(partNo) ? partNo : (!string.IsNullOrWhiteSpace(p2) ? p2 : p1),
            OldRevision = !string.IsNullOrWhiteSpace(oldRev) ? oldRev : r1,
            NewRevision = !string.IsNullOrWhiteSpace(newRev) ? newRev : r2,
            OldFileName = Path.GetFileName(oldPath),
            NewFileName = Path.GetFileName(newPath),
            OldFilePath = oldPath,
            NewFilePath = newPath
        };

        var d1 = ParseDxfInfo(oldPath, oldExtract);
        var d2 = ParseDxfInfo(newPath, newExtract);

        // 1. Layer differences
        foreach (var lay in d2.Layers.Except(d1.Layers, StringComparer.OrdinalIgnoreCase))
        {
            report.Items.Add(new RevisionDiffItem
            {
                Kind = RevisionDiffKind.Added,
                Category = "Katman",
                Property = lay,
                NewValue = lay,
                Description = L.F("'{0}' katmanı eklendi", lay)
            });
        }
        foreach (var lay in d1.Layers.Except(d2.Layers, StringComparer.OrdinalIgnoreCase))
        {
            report.Items.Add(new RevisionDiffItem
            {
                Kind = RevisionDiffKind.Removed,
                Category = "Katman",
                Property = lay,
                OldValue = lay,
                Description = L.F("'{0}' katmanı silindi", lay)
            });
        }

        // 2. Dimensions differences
        var oldDims = new List<double>(d1.Dimensions);
        var newDims = new List<double>(d2.Dimensions);

        for (int i = 0; i < Math.Min(oldDims.Count, newDims.Count); i++)
        {
            if (Math.Abs(oldDims[i] - newDims[i]) > 0.01)
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Modified,
                    Category = "Ölçü",
                    Property = L.F("Ölçü #{0}", i + 1),
                    OldValue = $"{oldDims[i]:0.##} mm",
                    NewValue = $"{newDims[i]:0.##} mm",
                    Description = L.F("Ölçü değişti: {0:0.##} mm → {1:0.##} mm", oldDims[i], newDims[i])
                });
            }
        }
        if (newDims.Count > oldDims.Count)
        {
            for (int i = oldDims.Count; i < newDims.Count; i++)
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Added,
                    Category = "Ölçü",
                    Property = L.F("Yeni Ölçü #{0}", i + 1),
                    NewValue = $"{newDims[i]:0.##} mm",
                    Description = L.F("Yeni ölçü eklendi: {0:0.##} mm", newDims[i])
                });
            }
        }

        // 3. Text differences
        foreach (var t2 in d2.Texts.Except(d1.Texts, StringComparer.OrdinalIgnoreCase))
        {
            // Check if there is a similar modified text in d1
            var match1 = d1.Texts.FirstOrDefault(t1 => IsSimilarText(t1, t2));
            if (match1 != null)
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Modified,
                    Category = "Yazı / Not",
                    Property = match1,
                    OldValue = match1,
                    NewValue = t2,
                    Description = $"{match1} → {t2}"
                });
            }
            else
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Added,
                    Category = "Yazı / Not",
                    Property = t2,
                    NewValue = t2,
                    Description = L.F("Yeni yazı/not eklendi: \"{0}\"", t2)
                });
            }
        }

        return report;
    }

    record DxfSummaryInfo(List<string> Layers, List<double> Dimensions, List<string> Texts, Dictionary<string, int> Counts);

    static DxfSummaryInfo ParseDxfInfo(string path, JsonElement? json)
    {
        if (json.HasValue)
        {
            var el = json.Value;
            var layers = new List<string>();
            if (el.TryGetProperty("layers", out var lArr))
                foreach (var item in lArr.EnumerateArray()) layers.Add(item.GetString() ?? "");

            var dims = new List<double>();
            if (el.TryGetProperty("dimensions", out var dArr))
                foreach (var item in dArr.EnumerateArray())
                    if (item.TryGetProperty("measurement", out var m) && m.ValueKind == JsonValueKind.Number)
                        dims.Add(m.GetDouble());

            var texts = new List<string>();
            if (el.TryGetProperty("texts", out var tArr))
                foreach (var item in tArr.EnumerateArray())
                    if (item.TryGetProperty("text", out var tv)) texts.Add(tv.GetString() ?? "");

            return new DxfSummaryInfo(layers, dims, texts, new());
        }

        // Lightweight fallback DXF parser
        if (File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dims = new List<double>();
            var texts = new List<string>();

            for (int i = 0; i < lines.Length - 1; i++)
            {
                var code = lines[i].Trim();
                var val = lines[i + 1].Trim();

                if (code == "8") // Layer name
                    layers.Add(val);
                else if (code == "1" || code == "3") // Text value
                {
                    if (val.Length > 0 && !val.StartsWith('$') && !val.StartsWith('<'))
                        texts.Add(val);
                }
                else if (code == "42") // Dimension measurement
                {
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        dims.Add(d);
                }
            }
            return new DxfSummaryInfo(layers.ToList(), dims, texts, new());
        }

        return new DxfSummaryInfo(new(), new(), new(), new());
    }

    static bool IsSimilarText(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        // If they share common prefix e.g. "8x Ø" or "DWG NO." or "Ra "
        var common = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) common++;
            else break;
        }
        return common >= 3;
    }

    // =========================================================================
    // PDF TECHNICAL DRAWING COMPARISON
    // =========================================================================

    public static RevisionDiffReport ComparePdf(
        string oldPath,
        string newPath,
        string partNo = "",
        string oldRev = "",
        string newRev = "",
        JsonElement? oldExtract = null,
        JsonElement? newExtract = null)
    {
        var (p1, r1) = ParsePartAndRevision(Path.GetFileName(oldPath));
        var (p2, r2) = ParsePartAndRevision(Path.GetFileName(newPath));

        var report = new RevisionDiffReport
        {
            FileType = "PDF",
            PartNumber = !string.IsNullOrWhiteSpace(partNo) ? partNo : (!string.IsNullOrWhiteSpace(p2) ? p2 : p1),
            OldRevision = !string.IsNullOrWhiteSpace(oldRev) ? oldRev : r1,
            NewRevision = !string.IsNullOrWhiteSpace(newRev) ? newRev : r2,
            OldFileName = Path.GetFileName(oldPath),
            NewFileName = Path.GetFileName(newPath),
            OldFilePath = oldPath,
            NewFilePath = newPath
        };

        var text1 = ExtractPdfText(oldPath, oldExtract);
        var text2 = ExtractPdfText(newPath, newExtract);

        var dims1 = ExtractDimensions(text1);
        var dims2 = ExtractDimensions(text2);

        // Compare dimensions
        foreach (var (key, val2) in dims2)
        {
            if (dims1.TryGetValue(key, out var val1))
            {
                if (!string.Equals(val1, val2, StringComparison.OrdinalIgnoreCase))
                {
                    report.Items.Add(new RevisionDiffItem
                    {
                        Kind = RevisionDiffKind.Modified,
                        Category = "Ölçü",
                        Property = key,
                        OldValue = val1,
                        NewValue = val2,
                        Description = $"{key}: {val1} → {val2}"
                    });
                }
            }
            else
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Added,
                    Category = "Ölçü",
                    Property = key,
                    NewValue = val2,
                    Description = L.F("Yeni ölçü eklendi: {0} ({1})", key, val2)
                });
            }
        }

        foreach (var (key, val1) in dims1)
        {
            if (!dims2.ContainsKey(key))
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Removed,
                    Category = "Ölçü",
                    Property = key,
                    OldValue = val1,
                    Description = L.F("Ölçü kaldırıldı: {0} ({1})", key, val1)
                });
            }
        }

        // Compare notes and text lines
        var lines1 = text1.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2).ToHashSet();
        var lines2 = text2.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2).ToHashSet();

        foreach (var l in lines2.Except(lines1).Take(10))
        {
            if (l.Contains("REV", StringComparison.OrdinalIgnoreCase) || l.Contains("DWG", StringComparison.OrdinalIgnoreCase))
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Modified,
                    Category = "Antet",
                    Property = "Revizyon / Başlık",
                    NewValue = l,
                    Description = l
                });
            }
            else if (!dims2.Values.Any(v => l.Contains(v)))
            {
                report.Items.Add(new RevisionDiffItem
                {
                    Kind = RevisionDiffKind.Added,
                    Category = "Not",
                    Property = l.Length > 30 ? l[..30] + "…" : l,
                    NewValue = l,
                    Description = l
                });
            }
        }

        return report;
    }

    static string ExtractPdfText(string path, JsonElement? json)
    {
        if (json.HasValue)
        {
            var sb = new StringBuilder();
            if (json.Value.TryGetProperty("content", out var cArr))
            {
                foreach (var p in cArr.EnumerateArray())
                    if (p.TryGetProperty("text", out var t)) sb.AppendLine(t.GetString());
            }
            return sb.ToString();
        }

        if (File.Exists(path))
        {
            // Simple text extraction from raw PDF stream tokens
            var raw = File.ReadAllText(path);
            var sb = new StringBuilder();
            var matches = Regex.Matches(raw, @"\(([^()]+)\)\s*T[jJ]");
            foreach (Match m in matches) sb.AppendLine(m.Groups[1].Value);
            return sb.ToString();
        }

        return "";
    }

    static Dictionary<string, string> ExtractDimensions(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Holes e.g. 8x Ø18 or Ø110
        var holeMatches = Regex.Matches(text, @"(?<label>\d+x\s*Ø\d+(\.\d+)?|Ø\d+(\.\d+)?(\s*[Hh]\d)?)");
        foreach (Match m in holeMatches)
        {
            var val = m.Value;
            var key = val.Contains('x') ? "Bağlantı Delikleri" : "Çap / Delik";
            if (!dict.ContainsKey(key)) dict[key] = val;
            else dict[key + " (" + val + ")"] = val;
        }

        // Tolerances e.g. 24 ±0.1 or 220 ±0.2
        var tolMatches = Regex.Matches(text, @"(?<nom>\d+(\.\d+)?)\s*±\s*(?<tol>\d+(\.\d+)?)");
        foreach (Match m in tolMatches)
        {
            var key = $"Boyut {m.Groups["nom"].Value}";
            dict[key] = m.Value;
        }

        // Surface roughness e.g. Ra 3.2
        var raMatches = Regex.Matches(text, @"Ra\s*(?<val>\d+(\.\d+)?)");
        foreach (Match m in raMatches)
        {
            dict["Yüzey Pürüzlülüğü (Ra)"] = m.Value;
        }

        return dict;
    }

    // =========================================================================
    // GENERAL COMPARER
    // =========================================================================

    public static async Task<RevisionDiffReport> CompareAsync(
        string oldPath,
        string newPath,
        Func<TechnicalWorker>? workerFactory = null,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(newPath).ToLowerInvariant();

        if (ext is ".xlsx" or ".xlsm")
            return await Task.Run(() => CompareExcel(oldPath, newPath), ct);

        if (ext is ".step" or ".stp")
        {
            JsonElement? extract1 = null, extract2 = null;
            if (workerFactory != null)
            {
                try
                {
                    extract1 = await workerFactory().CallAsync("extract_step", new { path = oldPath }, ct);
                    extract2 = await workerFactory().CallAsync("extract_step", new { path = newPath }, ct);
                }
                catch { }
            }
            return CompareStep(oldPath, newPath, oldExtract: extract1, newExtract: extract2);
        }

        if (ext is ".dxf" or ".dwg")
        {
            JsonElement? extract1 = null, extract2 = null;
            if (workerFactory != null)
            {
                try
                {
                    extract1 = await workerFactory().CallAsync("extract_dxf", new { path = oldPath }, ct);
                    extract2 = await workerFactory().CallAsync("extract_dxf", new { path = newPath }, ct);
                }
                catch { }
            }
            return CompareDxf(oldPath, newPath, oldExtract: extract1, newExtract: extract2);
        }

        if (ext == ".pdf")
        {
            JsonElement? extract1 = null, extract2 = null;
            if (workerFactory != null)
            {
                try
                {
                    extract1 = await workerFactory().CallAsync("extract_pdf", new { path = oldPath, ocr = true }, ct);
                    extract2 = await workerFactory().CallAsync("extract_pdf", new { path = newPath, ocr = true }, ct);
                }
                catch { }
            }
            return ComparePdf(oldPath, newPath, oldExtract: extract1, newExtract: extract2);
        }

        throw new NotSupportedException(L.F("Revizyon karşılaştırması bu dosya türünü desteklemiyor: {0}", ext));
    }
}

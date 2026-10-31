using System.Globalization;
using System.Text.RegularExpressions;

namespace MailStudio.Services.Nc;

public enum MoveKind { Rapid, Feed, Arc, Drill }

/// <summary>A straight piece of the tool path (arcs are split into short pieces), in machine units (mm).</summary>
public readonly record struct NcSegment(double X1, double Y1, double Z1, double X2, double Y2, double Z2, MoveKind Kind, int Line, int Tool, double Feed)
{
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1) + (Z2 - Z1) * (Z2 - Z1));
}

/// <summary>Per-tool totals.</summary>
public sealed class NcToolUse
{
    public int Tool { get; init; }
    public string Comment { get; set; } = "";
    public double FeedLength { get; set; }
    public double RapidLength { get; set; }
    public double Minutes { get; set; }
    public int FirstLine { get; set; }
    public double MinZ { get; set; } = double.PositiveInfinity;
    public int Holes { get; set; }
    public double MaxFeed { get; set; }
    public double MaxSpindle { get; set; }
}

/// <summary>Machine figures used for the time estimate (editable defaults).</summary>
public sealed class NcMachine
{
    public double RapidMmPerMin { get; set; } = 15000;
    public double ToolChangeSeconds { get; set; } = 8;
    /// <summary>Feed when a program moves with G1 before any F word (an error in the program, warned).</summary>
    public double DefaultFeed { get; set; } = 500;
}

/// <summary>
/// G-code reader for a backplot and a time estimate: ISO / Fanuc style (G0/G1/G2/G3 with I J K or R, G17-G19,
/// G90/G91, G20/G21, G81-G89 drilling cycles with G98/G99, G4 dwell, T/M6, S, F), Siemens comments and basic
/// Heidenhain conversational (L, CC, C, FMAX, TOOL CALL). Anything it cannot follow is listed as a warning rather
/// than guessed. The estimate ignores acceleration, so real cycle times are usually somewhat longer.
/// </summary>
public sealed class NcProgram
{
    public string FileName { get; init; } = "";
    public List<string> Lines { get; } = new();
    public List<NcSegment> Segments { get; } = new();
    public List<NcToolUse> Tools { get; } = new();
    public List<string> Warnings { get; } = new();
    public double FeedLength { get; private set; }
    public double RapidLength { get; private set; }
    public double FeedMinutes { get; private set; }
    public double RapidMinutes { get; private set; }
    public double DwellMinutes { get; private set; }
    public double ToolChangeMinutes { get; private set; }
    public double TotalMinutes => FeedMinutes + RapidMinutes + DwellMinutes + ToolChangeMinutes;
    public double MinX { get; private set; } = double.PositiveInfinity;
    public double MinY { get; private set; } = double.PositiveInfinity;
    public double MinZ { get; private set; } = double.PositiveInfinity;
    public double MaxX { get; private set; } = double.NegativeInfinity;
    public double MaxY { get; private set; } = double.NegativeInfinity;
    public double MaxZ { get; private set; } = double.NegativeInfinity;
    public bool Inch { get; private set; }
    public string Dialect { get; private set; } = "ISO";
    public bool HasMotion => Segments.Count > 0;

    static readonly Regex Word = new(@"([A-Z])\s*([-+]?(?:\d+\.?\d*|\.\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static NcProgram Parse(string text, string fileName = "", NcMachine? machine = null)
    {
        machine ??= new NcMachine();
        var p = new NcProgram { FileName = fileName };
        p.Lines.AddRange(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        if (p.Lines.Any(l => Regex.IsMatch(l, @"^\s*\d*\s*(BEGIN PGM|TOOL CALL|L\s+[XYZ]|CC\s+[XY])", RegexOptions.IgnoreCase))) { p.Dialect = "Heidenhain"; p.ParseHeidenhain(machine); }
        else p.ParseIso(machine);
        p.Finish();
        return p;
    }

    public static NcProgram Load(string path, NcMachine? machine = null)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { text = System.Text.Encoding.Latin1.GetString(bytes); }
        return Parse(text, Path.GetFileName(path), machine);
    }

    // ------------------------------------------------------------------ state
    double _x, _y, _z, _feed;
    int _tool, _nextTool, _plane = 17;
    bool _abs = true, _hasM6;
    double _scale = 1;
    readonly Dictionary<int, NcToolUse> _tools = new();
    readonly HashSet<string> _warned = new();

    NcToolUse Tool(int line)
    {
        if (!_tools.TryGetValue(_tool, out var t)) { t = new NcToolUse { Tool = _tool, FirstLine = line }; _tools[_tool] = t; Tools.Add(t); }
        return t;
    }

    void Warn(string key, string text) { if (_warned.Add(key)) Warnings.Add(text); }

    void Move(double x, double y, double z, MoveKind kind, int line, NcMachine m)
    {
        var s = new NcSegment(_x, _y, _z, x, y, z, kind, line, _tool, kind == MoveKind.Rapid ? m.RapidMmPerMin : _feed);
        var len = s.Length;
        _x = x; _y = y; _z = z;
        if (len < 1e-9) return;
        Segments.Add(s);
        var t = Tool(line);
        if (kind == MoveKind.Rapid) { RapidLength += len; RapidMinutes += len / m.RapidMmPerMin; t.RapidLength += len; t.Minutes += len / m.RapidMmPerMin; }
        else
        {
            var f = _feed;
            if (f <= 0) { Warn("nofeed", L.F("Satır {0}: ilerleme (F) verilmeden kesme hareketi; tahmin için {1} mm/dk alındı.", line + 1, m.DefaultFeed)); f = m.DefaultFeed; }
            FeedLength += len; FeedMinutes += len / f; t.FeedLength += len; t.Minutes += len / f;
            t.MaxFeed = Math.Max(t.MaxFeed, f);
            t.MinZ = Math.Min(t.MinZ, Math.Min(s.Z1, s.Z2));
        }
    }

    // ------------------------------------------------------------------ ISO / Fanuc / Siemens
    void ParseIso(NcMachine m)
    {
        // with M6 in the program a T word only preloads the next tool ("T1 M6 T2"); lathes (no M6) change on T
        _hasM6 = Lines.Any(l => Regex.IsMatch(StripComments(l, out _), @"M0?6(?!\d)"));
        int motion = 0; int retract = 98; double rPlane = double.NaN, cycleZ = double.NaN, q = 0; double initialZ = 0; double pDwell = 0;
        for (var i = 0; i < Lines.Count; i++)
        {
            var raw = Lines[i];
            var code = StripComments(raw, out var comment);
            if (code.Length == 0)
            {
                if (comment.Length > 0 && _tools.TryGetValue(_tool, out var tc) && tc.Comment.Length == 0 && Regex.IsMatch(comment, @"T\d|TOOL|TAKIM|D\s?=|FREZE|MATKAP|DRILL|MILL|ENDMILL", RegexOptions.IgnoreCase)) tc.Comment = comment;
                continue;
            }
            if (code.StartsWith('%') || code.StartsWith(':')) continue;
            if (Regex.IsMatch(code, @"\b(CYCLE\d+|POCKET\d|SLOT\d|HOLES\d|LONGHOLE|MCALL|CALL|REPEAT|GOTO[FB]?|IF|WHILE|#\d+\s*=)", RegexOptions.IgnoreCase))
                Warn("cycle:" + Regex.Match(code, @"CYCLE\d+|POCKET\d|SLOT\d|HOLES\d|LONGHOLE|MCALL|CALL|REPEAT|GOTO[FB]?|IF|WHILE|#\d+", RegexOptions.IgnoreCase).Value.ToUpperInvariant(),
                    L.F("Satır {0}: \"{1}\" (alt program / döngü / değişken) izlenemiyor; yol bu noktada eksik olabilir.", i + 1, code.Trim()));
            double? X = null, Y = null, Z = null, I = null, J = null, K = null, R = null, Q = null, P = null;
            var gs = new List<double>(); var ms = new List<int>();
            foreach (Match w in Word.Matches(code))
            {
                var letter = char.ToUpperInvariant(w.Groups[1].Value[0]);
                if (!double.TryParse(w.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                switch (letter)
                {
                    case 'G': gs.Add(v); break;
                    case 'M':
                        ms.Add((int)v);
                        if ((int)v == 6) { _tool = _nextTool; ToolChangeMinutes += m.ToolChangeSeconds / 60; Tool(i); }
                        break;
                    case 'X': X = v; break; case 'Y': Y = v; break; case 'Z': Z = v; break;
                    case 'I': I = v; break; case 'J': J = v; break; case 'K': K = v; break;
                    case 'R': R = v; break; case 'Q': Q = v; break; case 'P': P = v; break;
                    case 'F': _feed = v * _scale; break;
                    case 'S': if (_tools.TryGetValue(_tool, out var ts)) ts.MaxSpindle = Math.Max(ts.MaxSpindle, v); break;
                    case 'T': _nextTool = (int)v; if (!_hasM6) _tool = _nextTool; break;
                }
            }
            var dwell = false; var home = false;
            foreach (var g in gs)
            {
                switch (g)
                {
                    case 0: motion = 0; break; case 1: motion = 1; break; case 2: motion = 2; break; case 3: motion = 3; break;
                    case 4: dwell = true; break;
                    case 17: _plane = 17; break; case 18: _plane = 18; break; case 19: _plane = 19; break;
                    case 20: _scale = 25.4; Inch = true; break; case 21: _scale = 1; break;
                    case 28: home = true; break;
                    case 90: _abs = true; break; case 91: _abs = false; break;
                    case 98: retract = 98; break; case 99: retract = 99; break;
                    case 80: motion = 0; rPlane = cycleZ = double.NaN; break;
                    case 81 or 82 or 83 or 73 or 84 or 85 or 86 or 87 or 88 or 89: motion = (int)g; initialZ = _z; break;
                    case 54 or 55 or 56 or 57 or 58 or 59 or 40 or 41 or 42 or 43 or 49 or 94 or 95 or 15 or 16 or 61 or 64 or 64.1: break;
                    case 68 or 68.2 or 51 or 50.1 or 51.1:
                        Warn("g" + g, L.F("Satır {0}: G{1} (döndürme / ölçek / ayna) hesaba katılmadı.", i + 1, g.ToString(CultureInfo.InvariantCulture))); break;
                }
            }
            if (comment.Length > 0 && (ms.Contains(6) || (!_hasM6 && Regex.IsMatch(code, @"(^|\s)T\d"))))
            { var tu = Tool(i); if (tu.Comment.Length == 0) tu.Comment = comment; }
            if (ms.Contains(98)) Warn("m98", L.F("Satır {0}: M98 alt program çağrısı izlenemiyor; alt programın yolu çizilmedi.", i + 1));
            if (dwell) { DwellMinutes += (P ?? 0) / (P > 100 ? 60000.0 : 60.0); continue; }
            double T(double? v, double cur) => v is { } d ? (_abs ? d * _scale : cur + d * _scale) : cur;
            if (home)
            {
                // G28 goes to the reference point through the given point: only the Z retract is drawn
                if (Z != null) Move(_x, _y, T(Z, _z), MoveKind.Rapid, i, m);
                continue;
            }
            if (X == null && Y == null && Z == null && !(motion is 2 or 3 && (I != null || J != null || K != null))) continue;
            var nx = T(X, _x); var ny = T(Y, _y); var nz = T(Z, _z);
            switch (motion)
            {
                case 0: Move(nx, ny, nz, MoveKind.Rapid, i, m); break;
                case 1: Move(nx, ny, nz, MoveKind.Feed, i, m); break;
                case 2 or 3: Arc(nx, ny, nz, I * _scale, J * _scale, K * _scale, R * _scale, motion == 2, i, m); break;
                default:
                    {
                        // drilling cycle: position at the start height, rapid to R, feed to Z, back to R (G99) or to the start height (G98)
                        if (R != null) rPlane = _abs ? R.Value * _scale : initialZ + R.Value * _scale;
                        if (Z != null) cycleZ = _abs ? Z.Value * _scale : (double.IsNaN(rPlane) ? _z : rPlane) + Z.Value * _scale;
                        if (Q != null) q = Math.Abs(Q.Value * _scale);
                        if (double.IsNaN(rPlane) || double.IsNaN(cycleZ)) { Warn("cycle-rz", L.F("Satır {0}: delme döngüsünde R veya Z yok.", i + 1)); break; }
                        var top = _z;
                        Move(nx, ny, top, MoveKind.Rapid, i, m);
                        Move(nx, ny, rPlane, MoveKind.Rapid, i, m);
                        if (motion is 83 or 73 && q > 0)
                        {
                            for (var d = rPlane - q; d > cycleZ; d -= q) { Move(nx, ny, d, MoveKind.Drill, i, m); Move(nx, ny, motion == 83 ? rPlane : d + Math.Min(q, 1), MoveKind.Rapid, i, m); if (motion == 83) Move(nx, ny, d, MoveKind.Rapid, i, m); }
                        }
                        Move(nx, ny, cycleZ, MoveKind.Drill, i, m);
                        if (motion is 82 or 89 && P is { } dw) DwellMinutes += dw / (dw > 100 ? 60000.0 : 60.0);
                        Move(nx, ny, retract == 98 ? Math.Max(top, rPlane) : rPlane, motion is 84 or 85 or 86 or 88 or 89 ? MoveKind.Drill : MoveKind.Rapid, i, m);
                        Tool(i).Holes++;
                        break;
                    }
            }
        }
    }

    void Arc(double ex, double ey, double ez, double? I, double? J, double? K, double? R, bool cw, int line, NcMachine m)
    {
        // work in the active plane: (a, b) are the plane axes, c the helix axis
        (double a0, double b0, double c0, double a1, double b1, double c1, double? ia, double? ib) = _plane switch
        {
            18 => (_z, _x, _y, ez, ex, ey, K, I),
            19 => (_y, _z, _x, ey, ez, ex, J, K),
            _ => (_x, _y, _z, ex, ey, ez, I, J),
        };
        double ca, cb;
        if (R is { } r && ia == null && ib == null)
        {
            var dx = a1 - a0; var dy = b1 - b0; var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9 || Math.Abs(r) * 2 < d - 1e-6) { Warn("arcR" + line, L.F("Satır {0}: yay yarıçapı uç noktalarına uymuyor; düz çizildi.", line + 1)); Move(ex, ey, ez, MoveKind.Feed, line, m); return; }
            var h = Math.Sqrt(Math.Max(0, r * r - d * d / 4));
            var mx = (a0 + a1) / 2; var my = (b0 + b1) / 2;
            // R > 0: the shorter arc; the centre is on the right for G2 (clockwise)
            var sign = (cw ? 1 : -1) * (r > 0 ? 1 : -1);
            ca = mx + sign * h * dy / d; cb = my - sign * h * dx / d;
        }
        else { ca = a0 + (ia ?? 0); cb = b0 + (ib ?? 0); }
        var rad0 = Math.Sqrt((a0 - ca) * (a0 - ca) + (b0 - cb) * (b0 - cb));
        var rad1 = Math.Sqrt((a1 - ca) * (a1 - ca) + (b1 - cb) * (b1 - cb));
        if (rad0 < 1e-9) { Move(ex, ey, ez, MoveKind.Feed, line, m); return; }
        if (Math.Abs(rad0 - rad1) > Math.Max(0.01, rad0 * 0.002)) Warn("arcmis" + line, L.F("Satır {0}: yayın başlangıç ve bitiş yarıçapı farklı ({1:0.###} / {2:0.###} mm).", line + 1, rad0, rad1));
        var t0 = Math.Atan2(b0 - cb, a0 - ca); var t1 = Math.Atan2(b1 - cb, a1 - ca);
        var sweep = t1 - t0;
        if (cw) { if (sweep >= -1e-9) sweep -= 2 * Math.PI; }
        else { if (sweep <= 1e-9) sweep += 2 * Math.PI; }
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) * rad0 / 0.5), 6, 720);
        for (var s = 1; s <= steps; s++)
        {
            var t = t0 + sweep * s / steps;
            var a = ca + rad0 * Math.Cos(t); var b = cb + rad0 * Math.Sin(t); var c = c0 + (c1 - c0) * s / steps;
            if (s == steps) { a = a1; b = b1; c = c1; }
            var (x, y, z) = _plane switch { 18 => (b, c, a), 19 => (c, a, b), _ => (a, b, c) };
            Move(x, y, z, MoveKind.Arc, line, m);
        }
    }

    static string StripComments(string line, out string comment)
    {
        var c = new List<string>();
        var s = Regex.Replace(line, @"\(([^)]*)\)", mm => { c.Add(mm.Groups[1].Value.Trim()); return " "; });
        var semi = s.IndexOf(';');
        if (semi >= 0) { c.Add(s[(semi + 1)..].Trim()); s = s[..semi]; }
        comment = string.Join(" ", c.Where(x => x.Length > 0));
        s = Regex.Replace(s, @"^\s*/?\s*N\d+\s*", "", RegexOptions.IgnoreCase);   // block delete / sequence number
        return s.Trim().ToUpperInvariant();
    }

    // ------------------------------------------------------------------ Heidenhain conversational (basic)
    void ParseHeidenhain(NcMachine m)
    {
        double ccx = 0, ccy = 0;
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Regex.Replace(Lines[i], @"^\s*\d+\s*", "");
            var semi = line.IndexOf(';'); if (semi >= 0) line = line[..semi];
            line = line.Trim().ToUpperInvariant();
            if (line.Length == 0 || line.StartsWith("BEGIN PGM") || line.StartsWith("END PGM") || line.StartsWith("BLK FORM")) continue;
            var tc = Regex.Match(line, @"^TOOL CALL\s+(\d+)");
            if (tc.Success) { _tool = int.Parse(tc.Groups[1].Value, CultureInfo.InvariantCulture); ToolChangeMinutes += m.ToolChangeSeconds / 60; Tool(i); continue; }
            if (Regex.IsMatch(line, @"^(CYCL|CALL|LBL|FN|Q\d)")) { Warn("hh:" + line.Split(' ')[0], L.F("Satır {0}: \"{1}\" (döngü / alt program / parametre) izlenemiyor.", i + 1, line)); continue; }
            double? Get(string axis)
            {
                var mm = Regex.Match(line, @"\b(I?)" + axis + @"([-+]?\d*\.?\d+)");
                if (!mm.Success) return null;
                var v = double.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture);
                return mm.Groups[1].Value == "I" ? v + (axis == "X" ? _x : axis == "Y" ? _y : _z) : v;
            }
            var f = Regex.Match(line, @"\bF(MAX|\d+\.?\d*)");
            var rapid = f.Success && f.Groups[1].Value == "MAX";
            if (f.Success && !rapid) _feed = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
            if (line.StartsWith("CC"))
            {
                ccx = Get("X") ?? _x; ccy = Get("Y") ?? _y; continue;
            }
            if (line.StartsWith("L ") || line == "L")
            {
                Move(Get("X") ?? _x, Get("Y") ?? _y, Get("Z") ?? _z, rapid ? MoveKind.Rapid : MoveKind.Feed, i, m);
                continue;
            }
            if (line.StartsWith("C ") && (line.Contains("DR-") || line.Contains("DR+")))
            {
                var ex = Get("X") ?? _x; var ey = Get("Y") ?? _y; var ez = Get("Z") ?? _z;
                Arc(ex, ey, ez, ccx - _x, ccy - _y, null, null, line.Contains("DR-"), i, m);
            }
        }
    }

    void Finish()
    {
        foreach (var s in Segments)
        {
            MinX = Math.Min(MinX, Math.Min(s.X1, s.X2)); MaxX = Math.Max(MaxX, Math.Max(s.X1, s.X2));
            MinY = Math.Min(MinY, Math.Min(s.Y1, s.Y2)); MaxY = Math.Max(MaxY, Math.Max(s.Y1, s.Y2));
            MinZ = Math.Min(MinZ, Math.Min(s.Z1, s.Z2)); MaxZ = Math.Max(MaxZ, Math.Max(s.Z1, s.Z2));
        }
        if (!HasMotion) { MinX = MinY = MinZ = MaxX = MaxY = MaxZ = 0; Warnings.Add(L.T("Programda izlenebilen hareket yok.")); }
        Tools.RemoveAll(t => t.FeedLength == 0 && t.RapidLength == 0 && t.Holes == 0 && Tools.Count > 1);
        foreach (var t in Tools) if (double.IsInfinity(t.MinZ)) t.MinZ = 0;
    }

    /// <summary>Text summary for the reply / clipboard.</summary>
    public string Summary() =>
        L.F("{0}: tahmini süre {1}, kesme yolu {2:0} mm, hızlı yol {3:0} mm, {4} takım, X {5:0.###}…{6:0.###} Y {7:0.###}…{8:0.###} Z {9:0.###}…{10:0.###} mm",
            FileName, FormatMinutes(TotalMinutes), FeedLength, RapidLength, Tools.Count, MinX, MaxX, MinY, MaxY, MinZ, MaxZ);

    public static string FormatMinutes(double minutes)
    {
        var t = TimeSpan.FromMinutes(minutes);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

/// <summary>Differences between two NC programs: changed lines and the effect on time, path and extents.</summary>
public sealed class NcComparison
{
    public NcProgram Old { get; }
    public NcProgram New { get; }
    public List<(int OldLine, int NewLine, string Text, char Kind)> Lines { get; } = new();   // Kind: ' ', '-', '+'
    public int Added => Lines.Count(l => l.Kind == '+');
    public int Removed => Lines.Count(l => l.Kind == '-');

    public NcComparison(NcProgram oldProgram, NcProgram newProgram)
    {
        Old = oldProgram; New = newProgram;
        static string Key(string s) => Regex.Replace(Regex.Replace(s, @"^\s*N\d+\s*", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim().ToUpperInvariant();
        var a = Old.Lines.Select(Key).ToArray(); var b = New.Lines.Select(Key).ToArray();
        // LCS on lines (programs are at most a few hundred thousand lines; trimmed common head/tail keeps the table small)
        int start = 0; while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length; while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        for (var i = 0; i < start; i++) Lines.Add((i, i, New.Lines[i], ' '));
        var n = endA - start; var m = endB - start;
        if ((long)n * m > 25_000_000)
        {
            for (var i = start; i < endA; i++) Lines.Add((i, -1, Old.Lines[i], '-'));
            for (var j = start; j < endB; j++) Lines.Add((-1, j, New.Lines[j], '+'));
        }
        else
        {
            var dp = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    dp[i, j] = a[start + i] == b[start + j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { Lines.Add((start + x, start + y, New.Lines[start + y], ' ')); x++; y++; }
                else if (y < m && (x == n || dp[x, y + 1] >= dp[x + 1, y])) { Lines.Add((-1, start + y, New.Lines[start + y], '+')); y++; }
                else { Lines.Add((start + x, -1, Old.Lines[start + x], '-')); x++; }
            }
        }
        for (int i = endA, j = endB; i < a.Length; i++, j++) Lines.Add((i, j, New.Lines[j], ' '));
    }

    public string Summary() => L.F("{0} satır eklendi, {1} satır silindi. Süre {2} → {3} ({4:+0.0;-0.0;0} dk), kesme yolu {5:0} → {6:0} mm, en derin Z {7:0.###} → {8:0.###} mm.",
        Added, Removed, NcProgram.FormatMinutes(Old.TotalMinutes), NcProgram.FormatMinutes(New.TotalMinutes), New.TotalMinutes - Old.TotalMinutes,
        Old.FeedLength, New.FeedLength, Old.MinZ, New.MinZ);
}

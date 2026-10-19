using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// 2D CAD viewer: DXF and DWG (DWG → DXF: ODA File Converter if installed, otherwise the bundled LibreDWG).
/// Vector drawing: unlimited zoom, layer toggles, Model/Paper layouts, distance measuring with corner snapping,
/// text/dimension search, light/dark background. Never writes to the file.
/// </summary>
public sealed class CadDrawingWindow : Window
{
    readonly string _path; readonly TechnicalWorker _worker; readonly CancellationTokenSource _life = new();
    readonly CadCanvas _canvas = new();
    readonly ListBox _layers = new() { BorderThickness = new Thickness(0) };
    readonly ComboBox _layout = new() { Width = 150, Margin = new Thickness(4, 0, 4, 0) };
    readonly TextBox _search = new() { Width = 170, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("Yazı ve ölçü değerlerinde ara (Enter)") };
    readonly ToggleButton _measure = new(), _dark = new();
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _coords = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    readonly ColumnDefinition _layerCol = new() { Width = new GridLength(220) };
    string _units = ""; bool _loading;
    List<(string text, double x, double y, double h)> _texts = new();
    int _hit = -1; List<int> _hits = new();

    public CadDrawingWindow(string path, string? workerExecutable = null)
    {
        _path = path; _worker = new(workerExecutable);
        Title = Path.GetFileName(path) + " — " + (Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ? "DWG" : "DXF");
        Width = 1320; Height = 860; MinWidth = 760; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid();
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new() { Height = h });
        Content = root;
        root.Children.Add(BuildToolbar());
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.ColumnDefinitions.Add(_layerCol); body.ColumnDefinitions.Add(new());
        var left = new DockPanel { Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var lh = new DockPanel { Margin = new Thickness(10, 8, 6, 6) }; DockPanel.SetDock(lh, Dock.Top);
        var allOn = new Button { Content = L.T("Tümü"), Padding = new Thickness(6, 1, 6, 1), ToolTip = L.T("Tüm katmanları göster") }; allOn.Click += (_, _) => SetAllLayers(true);
        DockPanel.SetDock(allOn, Dock.Right); lh.Children.Add(allOn);
        lh.Children.Add(new TextBlock { Text = L.T("Katmanlar"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(lh); left.Children.Add(_layers);
        body.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Child = left });
        Grid.SetColumn(_canvas, 1); body.Children.Add(_canvas);
        var status = new Border { Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var sd = new DockPanel(); status.Child = sd; DockPanel.SetDock(_coords, Dock.Right); _coords.Foreground = ThemeManager.Brush("TextSecondaryBrush"); sd.Children.Add(_coords); sd.Children.Add(_status);
        Grid.SetRow(status, 2); root.Children.Add(status);

        _canvas.Dark = AppSettings.Shared.CadScene == "Dark";
        _dark.IsChecked = _canvas.Dark;
        _canvas.CursorMoved += p => _coords.Text = $"X {Fmt(p.X)}   Y {Fmt(p.Y)} {_units}";
        _canvas.Measured += (a, b) =>
        {
            var d = b - a; var ang = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
            _status.Text = L.F("Mesafe {0} {1}   ΔX {2}   ΔY {3}   Açı {4:0.###}°   (Esc: temizle)", Fmt(d.Length), _units, Fmt(d.X), Fmt(d.Y), ang);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _canvas.ClearMeasure(); _measure.IsChecked = false; }
            else if (e.Key == Key.F && !_search.IsKeyboardFocusWithin) _canvas.Fit();
            else if (e.Key == Key.M && !_search.IsKeyboardFocusWithin) _measure.IsChecked = !(_measure.IsChecked == true);
            else if (e.Key == Key.F3) NextHit();
            else return;
            e.Handled = true;
        };
        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) => { _life.Cancel(); _worker.Dispose(); };
    }

    static string Fmt(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    UIElement BuildToolbar()
    {
        var bar = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var w = new WrapPanel(); bar.Child = w;
        StackPanel Label(string glyph, string? text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
        void B(string glyph, string tip, Action a, string? text = null)
        {
            var b = new Button { Content = Label(glyph, text), ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(7, 3, 7, 3), MinHeight = 28 };
            if (TryFindResource("SubtleButton") is Style s) b.Style = s;
            b.Click += (_, _) => a(); w.Children.Add(b);
        }
        void Sep() => w.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), Background = ThemeManager.Brush("BorderSubtleBrush"), VerticalAlignment = VerticalAlignment.Center });
        B("", L.T("Katman panelini göster/gizle"), () => _layerCol.Width = new GridLength(_layerCol.Width.Value > 0 ? 0 : 220));
        w.Children.Add(new TextBlock { Text = L.T("Düzen:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        _layout.SelectionChanged += async (_, _) => { if (!_loading && _layout.SelectedItem is string l) await RenderAsync(l); };
        w.Children.Add(_layout);
        Sep();
        B("", L.T("Sığdır (F)"), () => _canvas.Fit(), L.T("Sığdır"));
        B("", L.T("Yakınlaştır"), () => _canvas.ZoomBy(1.4));
        B("", L.T("Uzaklaştır"), () => _canvas.ZoomBy(1 / 1.4));
        Sep();
        _measure.Content = Label("", L.T("Ölç")); _measure.ToolTip = L.T("Mesafe ölç (M): iki noktaya tıklayın; köşe ve uç noktalara yakalar");
        if (TryFindResource("MsSegmentToggleButtonStyle") is Style ts) { _measure.Style = ts; _dark.Style = ts; }
        _measure.Checked += (_, _) => { _canvas.MeasureMode = true; _status.Text = L.T("Ölçme: ilk noktaya tıklayın (uç noktalara yakalar). Esc ile çıkın."); };
        _measure.Unchecked += (_, _) => { _canvas.MeasureMode = false; _canvas.ClearMeasure(); };
        w.Children.Add(_measure);
        _dark.Content = Label("", L.T("Koyu zemin")); _dark.ToolTip = L.T("Çizim zeminini koyu/açık yap");
        _dark.Checked += (_, _) => { _canvas.Dark = true; Save("Dark"); }; _dark.Unchecked += (_, _) => { _canvas.Dark = false; Save("Light"); };
        w.Children.Add(_dark);
        Sep();
        var hint = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var ph = new TextBlock { Text = L.T("Yazı / ölçü ara"), Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("TextMutedBrush") };
        _search.TextChanged += (_, _) => ph.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Search(); e.Handled = true; } };
        hint.Children.Add(_search); hint.Children.Add(ph); w.Children.Add(hint);
        B("", L.T("Çizimdeki tüm yazıları panoya kopyala"), CopyTexts);
        Sep();
        B("", L.T("Varsayılan CAD programında aç"), () => { try { Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); } catch (Exception ex) { _status.Text = ex.Message; } }, L.T("Programda aç"));
        B("⚖", L.T("Önceki revizyonla karşılaştır"), async () => await CompareRevisionAsync(), L.T("Revizyon Karşılaştır"));
        return bar;
    }

    static void Save(string scene) { AppSettings.Shared.CadScene = scene; try { AppSettings.Shared.Save(); } catch { } }

    Task<JsonElement> Call(string op, object args) => Task.Run(() => _worker.CallAsync(op, args, _life.Token));

    async Task OpenAsync()
    {
        var dwg = Path.GetExtension(_path).Equals(".dwg", StringComparison.OrdinalIgnoreCase);
        _status.Text = dwg ? L.T("DWG okunuyor (DXF'e çevriliyor)…") : L.T("DXF okunuyor…");
        try
        {
            var info = await Call("cad2d_open", new { path = _path });
            _units = info.GetProperty("units").GetString() ?? "";
            _loading = true; _layout.Items.Clear();
            foreach (var l in info.GetProperty("layouts").EnumerateArray()) _layout.Items.Add(l.GetString()!);
            _layout.SelectedIndex = 0; _loading = false;
            _layers.Items.Clear();
            foreach (var l in info.GetProperty("layers").EnumerateArray())
            {
                var name = l.GetProperty("name").GetString()!; var on = l.GetProperty("on").GetBoolean();
                var cb = new CheckBox { IsChecked = on, Tag = name, VerticalContentAlignment = VerticalAlignment.Center };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Background = Hex(l.GetProperty("color").GetString()) });
                sp.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 });
                cb.Content = sp;
                cb.Checked += (_, _) => _canvas.SetLayer(name, true); cb.Unchecked += (_, _) => _canvas.SetLayer(name, false);
                if (!on) _canvas.SetLayer(name, false);
                _layers.Items.Add(cb);
            }
            var conv = info.TryGetProperty("converted", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            await RenderAsync("Model");
            _status.Text = L.F("{0}DXF {1} · {2} nesne · Tekerlek: yakınlaştır · sol/orta sürükle: kaydır · M: ölç · F: sığdır", (conv != null ? $"DWG → DXF ({conv}) · " : ""), info.GetProperty("version").GetString(), info.GetProperty("entities").GetInt32());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_life.IsCancellationRequested) return;
            _status.Text = L.T("Açılamadı: ") + ex.Message;
            MessageBox.Show(this, L.T("Çizim açılamadı:\n") + ex.Message + (dwg ? L.T("\n\nİpucu: Ücretsiz \"ODA File Converter\" kuruluysa MailStudio onu otomatik kullanır.") : ""), "MailStudio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static Brush Hex(string? h) { try { return (Brush)new BrushConverter().ConvertFromString(h ?? "#000000")!; } catch { return Brushes.Black; } }

    async Task RenderAsync(string layout)
    {
        _status.Text = L.F("\"{0}\" çiziliyor…", layout);
        var r = await Call("cad2d_render", new { layout });
        var model = await Task.Run(() => CadModel.From(r));
        _canvas.Load(model);
        _texts = r.TryGetProperty("texts", out var t) ? t.EnumerateArray().Select(x => (x.GetProperty("text").GetString() ?? "", x.GetProperty("x").GetDouble(), x.GetProperty("y").GetDouble(), x.GetProperty("h").GetDouble())).ToList() : new();
        _hits.Clear(); _hit = -1;
        _status.Text = L.F("{0}: {1} çizim öğesi, {2} yazı.", layout, r.GetProperty("count").GetInt32(), _texts.Count);
    }

    void SetAllLayers(bool on) { foreach (CheckBox cb in _layers.Items) cb.IsChecked = on; }

    void Search()
    {
        var q = _search.Text.Trim(); _hits.Clear(); _hit = -1; _canvas.Highlights = new();
        if (q.Length == 0) { _canvas.InvalidateVisual(); return; }
        for (int i = 0; i < _texts.Count; i++) if (_texts[i].text.Contains(q, StringComparison.CurrentCultureIgnoreCase)) _hits.Add(i);
        _canvas.Highlights = _hits.Select(i => new Point(_texts[i].x, _texts[i].y)).ToList();
        if (_hits.Count == 0) { _status.Text = L.F("\"{0}\" bulunamadı.", q); _canvas.InvalidateVisual(); return; }
        NextHit();
    }

    void NextHit()
    {
        if (_hits.Count == 0) return;
        _hit = (_hit + 1) % _hits.Count; var t = _texts[_hits[_hit]];
        _canvas.CenterOn(new Point(t.x, t.y), Math.Max(t.h * 30, 1e-6));
        _status.Text = $"\"{_search.Text.Trim()}\": {_hit + 1}/{_hits.Count} — {t.text}   " + L.T("(F3 sonraki)");
    }

    void CopyTexts()
    {
        if (_texts.Count == 0) { _status.Text = L.T("Çizimde yazı yok."); return; }
        try { Clipboard.SetText(string.Join(Environment.NewLine, _texts.Select(t => t.text))); _status.Text = L.F("{0} yazı panoya kopyalandı.", _texts.Count); } catch { }
    }

    async Task CompareRevisionAsync()
    {
        var candidates = RevisionService.FindCandidates(null, "", _path);
        string? oldPath = candidates.Count > 0 ? candidates[0].FilePath : null;
        if (string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Önceki revizyon CAD dosyasını seçin"),
                Filter = "CAD (*.dxf;*.dwg)|*.dxf;*.dwg|All files (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(_path)
            };
            if (dlg.ShowDialog(this) == true) oldPath = dlg.FileName;
        }
        if (string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath)) return;

        _status.Text = L.T("Önceki revizyonla karşılaştırılıyor...");
        try
        {
            var report = await RevisionService.CompareAsync(oldPath, _path, () => _worker, _life.Token);
            var win = new RevisionDiffWindow(report) { Owner = this };
            win.Show();
            _status.Text = L.F("Revizyon karşılaştırma tamamlandı: {0} fark bulundu.", report.TotalChanges);
        }
        catch (Exception ex)
        {
            _status.Text = L.T("Karşılaştırma hatası: ") + ex.Message;
        }
    }
}

/// <summary>Frozen WPF geometries built from the worker records.</summary>
public sealed class CadModel
{
    public sealed class Group { public string Layer = ""; public Color Color; public double Lw; public StreamGeometry? Lines, Fills; public List<Point> Points = new(); }
    public List<Group> Groups = new(); public Rect Bounds; public Point[] Vertices = Array.Empty<Point>();

    public static CadModel From(JsonElement r)
    {
        var m = new CadModel();
        var b = r.GetProperty("bbox").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        m.Bounds = new Rect(new Point(b[0], b[1]), new Point(b[2], b[3]));
        var verts = new List<Point>();
        foreach (var g in r.GetProperty("groups").EnumerateArray())
        {
            var grp = new Group { Layer = g.GetProperty("layer").GetString() ?? "0", Lw = g.GetProperty("lw").GetDouble() };
            try { grp.Color = (Color)ColorConverter.ConvertFromString(g.GetProperty("color").GetString() ?? "#000000"); } catch { grp.Color = Colors.Black; }
            var lines = g.GetProperty("lines");
            if (lines.GetArrayLength() > 0)
            {
                var sg = new StreamGeometry();
                using (var ctx = sg.Open())
                    foreach (var pl in lines.EnumerateArray())
                    {
                        var c = pl.EnumerateArray().Select(x => x.GetDouble()).ToArray(); if (c.Length < 4) continue;
                        ctx.BeginFigure(new Point(c[0], c[1]), false, false);
                        var pts = new List<Point>(c.Length / 2 - 1);
                        for (int i = 2; i + 1 < c.Length; i += 2) pts.Add(new Point(c[i], c[i + 1]));
                        ctx.PolyLineTo(pts, true, false);
                        verts.Add(new Point(c[0], c[1])); verts.Add(pts[^1]);
                        if (pts.Count <= 8) verts.AddRange(pts);   // short polyline corners (not the intermediate points of arcs)
                    }
                sg.Freeze(); grp.Lines = sg;
            }
            var fills = g.GetProperty("fills");
            if (fills.GetArrayLength() > 0)
            {
                var sg = new StreamGeometry { FillRule = FillRule.EvenOdd };   // letters and hatch islands stay as holes
                using (var ctx = sg.Open())
                    foreach (var set in fills.EnumerateArray())
                        foreach (var poly in set.EnumerateArray())
                        {
                            var c = poly.EnumerateArray().Select(x => x.GetDouble()).ToArray(); if (c.Length < 6) continue;
                            ctx.BeginFigure(new Point(c[0], c[1]), true, true);
                            var pts = new List<Point>(); for (int i = 2; i + 1 < c.Length; i += 2) pts.Add(new Point(c[i], c[i + 1]));
                            ctx.PolyLineTo(pts, false, false);
                        }
                sg.Freeze(); grp.Fills = sg;
            }
            var p = g.GetProperty("points").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            for (int i = 0; i + 1 < p.Length; i += 2) grp.Points.Add(new Point(p[i], p[i + 1]));
            verts.AddRange(grp.Points);
            m.Groups.Add(grp);
        }
        m.Vertices = verts.ToArray();
        return m;
    }
}

/// <summary>Vector drawing surface with pan/zoom and corner-snapping measurement (Y up, world coordinates).</summary>
public sealed class CadCanvas : FrameworkElement
{
    CadModel? _model; readonly HashSet<string> _hidden = new();
    double _scale = 1; Point _origin;   // world → screen: sx = (x - ox) * s, sy = (oy - y) * s
    Point? _drag; Point _dragOrigin; Point? _m1, _m2, _snap; Point _mouse;
    Dictionary<(int, int), List<Point>> _grid = new(); double _cell = 1;
    public bool MeasureMode; public List<Point> Highlights = new();
    bool _dark; public bool Dark { get => _dark; set { _dark = value; InvalidateVisual(); } }
    public event Action<Point>? CursorMoved; public event Action<Point, Point>? Measured;

    public CadCanvas() { ClipToBounds = true; Focusable = true; Cursor = Cursors.Cross; }

    public void Load(CadModel m)
    {
        _model = m; _m1 = _m2 = null; Highlights = new();
        var ext = Math.Max(m.Bounds.Width, m.Bounds.Height); _cell = Math.Max(ext / 150, 1e-9); _grid = new();
        foreach (var v in m.Vertices) { var k = ((int)Math.Floor(v.X / _cell), (int)Math.Floor(v.Y / _cell)); if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new(); if (l.Count < 64) l.Add(v); }
        if (ActualWidth > 0) Fit(); else Loaded += (_, _) => Fit();
    }

    public void SetLayer(string layer, bool on) { if (on) _hidden.Remove(layer); else _hidden.Add(layer); InvalidateVisual(); }

    public void Fit()
    {
        if (_model == null || ActualWidth <= 0) return;
        var b = _model.Bounds; if (b.Width <= 0 && b.Height <= 0) b.Inflate(1, 1);
        _scale = Math.Min((ActualWidth - 40) / Math.Max(b.Width, 1e-9), (ActualHeight - 40) / Math.Max(b.Height, 1e-9));
        _origin = new Point(b.X + b.Width / 2 - ActualWidth / 2 / _scale, b.Y + b.Height / 2 + ActualHeight / 2 / _scale);
        InvalidateVisual();
    }

    public void ZoomBy(double f) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), f);

    void ZoomAt(Point screen, double f)
    {
        var w = ToWorld(screen); _scale = Math.Clamp(_scale * f, 1e-9, 1e9);
        _origin = new Point(w.X - screen.X / _scale, w.Y + screen.Y / _scale); InvalidateVisual();
    }

    public void CenterOn(Point world, double span)
    {
        _scale = Math.Min(ActualWidth, ActualHeight) / span;
        _origin = new Point(world.X - ActualWidth / 2 / _scale, world.Y + ActualHeight / 2 / _scale); InvalidateVisual();
    }

    Point ToWorld(Point s) => new(_origin.X + s.X / _scale, _origin.Y - s.Y / _scale);
    Point ToScreen(Point w) => new((w.X - _origin.X) * _scale, (_origin.Y - w.Y) * _scale);

    public void ClearMeasure() { _m1 = _m2 = null; InvalidateVisual(); }

    Point? Snap(Point screen)
    {
        var w = ToWorld(screen); var r = 12 / _scale; var best = double.MaxValue; Point? hit = null;
        int cx = (int)Math.Floor(w.X / _cell), cy = (int)Math.Floor(w.Y / _cell), span = (int)Math.Ceiling(r / _cell);
        if (span > 6) span = 6;
        for (int i = cx - span; i <= cx + span; i++)
            for (int j = cy - span; j <= cy + span; j++)
                if (_grid.TryGetValue((i, j), out var l))
                    foreach (var v in l) { var d = (v - w).Length; if (d < r && d < best) { best = d; hit = v; } }
        return hit;
    }

    static Color Adapt(Color c, bool dark)
    {
        if (!dark) return c.R > 235 && c.G > 235 && c.B > 235 ? Colors.Black : c;   // a white line is invisible on a white background
        var lum = (.299 * c.R + .587 * c.G + .114 * c.B) / 255;
        if (lum < .2) return Color.FromRgb(230, 230, 230);
        return c;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = _dark ? Color.FromRgb(0x21, 0x25, 0x2B) : Colors.White;
        dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_model == null) return;
        dc.PushTransform(new MatrixTransform(_scale, 0, 0, -_scale, -_origin.X * _scale, _origin.Y * _scale));
        foreach (var g in _model.Groups)
        {
            if (_hidden.Contains(g.Layer)) continue;
            var c = Adapt(g.Color, _dark); var brush = new SolidColorBrush(c); brush.Freeze();
            if (g.Fills != null) dc.DrawGeometry(brush, null, g.Fills);
            if (g.Lines != null)
            {
                var px = g.Lw >= .5 ? 2.2 : g.Lw >= .3 ? 1.6 : 1.0;
                var pen = new Pen(brush, px / _scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze();
                dc.DrawGeometry(null, pen, g.Lines);
            }
        }
        dc.Pop();
        foreach (var g in _model.Groups)
            if (!_hidden.Contains(g.Layer))
                foreach (var p in g.Points) { var s = ToScreen(p); dc.DrawEllipse(new SolidColorBrush(Adapt(g.Color, _dark)), null, s, 1.5, 1.5); }
        var accent = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x10));
        foreach (var h in Highlights) { var s = ToScreen(h); dc.DrawEllipse(null, new Pen(accent, 2), s, 14, 14); }
        if (MeasureMode && _snap is { } sn) { var s = ToScreen(sn); dc.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 1.5), new Rect(s.X - 6, s.Y - 6, 12, 12)); }
        if (_m1 is { } a)
        {
            var b = _m2 ?? (MeasureMode ? (_snap ?? ToWorld(_mouse)) : a);
            var sa = ToScreen(a); var sb = ToScreen(b);
            var pen = new Pen(Brushes.DeepSkyBlue, 1.6) { DashStyle = _m2 == null ? DashStyles.Dash : DashStyles.Solid };
            dc.DrawLine(pen, sa, sb); dc.DrawEllipse(Brushes.DeepSkyBlue, null, sa, 3, 3); dc.DrawEllipse(Brushes.DeepSkyBlue, null, sb, 3, 3);
            var d = (b - a).Length;
            var ft = new FormattedText(d.ToString("0.###", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var mid = new Point((sa.X + sb.X) / 2, (sa.Y + sb.Y) / 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xDD, 0x0F, 0x6C, 0xBD)), null, new Rect(mid.X + 6, mid.Y - ft.Height - 6, ft.Width + 10, ft.Height + 4), 3, 3);
            dc.DrawText(ft, new Point(mid.X + 11, mid.Y - ft.Height - 4));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e) { ZoomAt(e.GetPosition(this), Math.Pow(1.0018, e.Delta)); e.Handled = true; }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus(); var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle && e.ClickCount == 2) { Fit(); return; }
        if (e.ChangedButton == MouseButton.Left && MeasureMode)
        {
            var w = Snap(p) ?? ToWorld(p);
            if (_m1 == null || _m2 != null) { _m1 = w; _m2 = null; }
            else { _m2 = w; Measured?.Invoke(_m1.Value, w); }
            InvalidateVisual(); return;
        }
        if (e.ChangedButton is MouseButton.Left or MouseButton.Middle) { _drag = p; _dragOrigin = _origin; CaptureMouse(); Cursor = Cursors.SizeAll; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        CursorMoved?.Invoke(ToWorld(_mouse));
        if (_drag is { } d) { var dv = _mouse - d; _origin = new Point(_dragOrigin.X - dv.X / _scale, _dragOrigin.Y + dv.Y / _scale); InvalidateVisual(); return; }
        if (MeasureMode) { _snap = Snap(_mouse); InvalidateVisual(); }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e) { if (_drag != null) { _drag = null; ReleaseMouseCapture(); Cursor = Cursors.Cross; } }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_model != null && info.PreviousSize.Width < 1) Fit();
    }
}

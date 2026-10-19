using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MailStudio.Services;

namespace MailStudio.Views;

public sealed partial class PdfAttachmentWindow
{
    enum Tool { Hand, Text, Object, Highlight, Underline, Strike, Ink, Rect, Note, AddText, Correct }

    /// <summary>A single unsaved edit. Coordinates are PDF points, unrotated page, origin top-left.</summary>
    sealed class PdfEdit
    {
        public int Page; public string Kind = "";
        public List<Rect>? Rects; public Rect Rect; public List<List<Point>>? Strokes; public Point At;
        public string? Text; public double Size = 11, Width = 1.6; public string Color = "#000000"; public double? Baseline;
        public int Index = -1; public double Dx, Dy;

        public Dictionary<string, object?> ToWorker()
        {
            static object R(Rect r) => new { x = r.X, y = r.Y, w = r.Width, h = r.Height };
            var d = new Dictionary<string, object?> { ["page"] = Page, ["kind"] = Kind, ["color"] = Color };
            switch (Kind)
            {
                case "highlight": case "underline": case "strikeout": d["rects"] = Rects!.Select(R).ToArray(); if (Text != null) d["note"] = Text; break;
                case "ink": d["strokes"] = Strokes!.Select(s => s.Select(p => new[] { p.X, p.Y }).ToArray()).ToArray(); d["width"] = Width; break;
                case "rect": d["rect"] = R(Rect); d["width"] = Width; break;
                case "note": d["x"] = At.X; d["y"] = At.Y; d["text"] = Text ?? ""; break;
                case "text": case "correct": d["rect"] = R(Rect); d["text"] = Text ?? ""; d["size"] = Size; if (Baseline is { } b) d["baseline"] = b; break;
                case "object_move": d["index"] = Index; d["dx"] = Dx; d["dy"] = Dy; break;
                case "object_delete": d["index"] = Index; break;
            }
            return d;
        }
    }

    sealed record UndoOp(Action Undo, Action Redo, string Name);

    static readonly (string Name, string Hex)[] Swatches =
    {
        (L.T("Sarı"), "#FFD400"), (L.T("Yeşil"), "#7FD858"), (L.T("Camgöbeği"), "#4CC2FF"), (L.T("Pembe"), "#FF7EB6"),
        (L.T("Kırmızı"), "#C42B1C"), (L.T("Mavi"), "#0F6CBD"), (L.T("Siyah"), "#000000"),
    };

    readonly List<PdfEdit> _edits = new();
    readonly Stack<UndoOp> _undo = new(), _redo = new();
    readonly Dictionary<Tool, string> _toolColor = new()
    {
        [Tool.Highlight] = "#FFD400", [Tool.Underline] = "#0F6CBD", [Tool.Strike] = "#C42B1C", [Tool.Ink] = "#C42B1C",
        [Tool.Rect] = "#C42B1C", [Tool.Note] = "#FFD400", [Tool.AddText] = "#000000", [Tool.Correct] = "#000000",
    };
    readonly Dictionary<Tool, RadioButton> _toolButtons = new();
    readonly WrapPanel _swatchPanel = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox _sizeBox = new() { Width = 56, IsEditable = true, ToolTip = L.T("Yazı boyutu (pt)") };
    readonly TextBox _searchBox = new() { Width = 170, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("Belgede ara (Ctrl+F, sonraki F3)") };
    Tool _tool = Tool.Text; bool _dirty;

    // selection state
    int _selPage = -1, _selA = -1, _selB = -1; bool _dragging; Point _dragStart;
    Rect? _areaSel; PdfObject? _selObject; PdfEdit? _selEdit; Polyline? _inkLine; List<Point>? _inkPoints;
    FrameworkElement? _inlineEditor; Action<string>? _inlineCommit;
    List<(int Page, List<Rect> Rects)> _hits = new(); int _hitIndex = -1;
    readonly Dictionary<int, Canvas> _hitLayers = new();

    // ------------------------------------------------------------------ toolbar parts

    UIElement BuildToolButtons()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(Tool t, string glyph, string tip)
        {
            var rb = new RadioButton { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15 }, ToolTip = tip, GroupName = "pdftool" + GetHashCode(), Width = 32, Height = 30, Padding = new Thickness(0) };
            if (TryFindResource("SegmentRadio") is Style s) rb.Style = s;
            rb.Checked += (_, _) => SetTool(t);
            _toolButtons[t] = rb; panel.Children.Add(rb);
        }
        Add(Tool.Hand, "", L.T("El: sürükleyerek kaydır (orta tuş her araçta çalışır)"));
        Add(Tool.Text, "", L.T("Metin seç: sürükle, çift tıkla kelime, Ctrl+A sayfa, Ctrl+C kopyala"));
        Add(Tool.Object, "", L.T("Nesne seç: görsel, çizgi, metin bloğu veya notu seç; sürükleyerek taşı, Delete ile sil"));
        Add(Tool.Highlight, "", L.T("Vurgula: metni sürükleyerek işaretle (metin yoksa alan)"));
        Add(Tool.Underline, "", L.T("Altını çiz"));
        Add(Tool.Strike, "", L.T("Üstünü çiz"));
        Add(Tool.Ink, "", L.T("Kalem: serbest çizim"));
        Add(Tool.Rect, "", L.T("Dikdörtgen ile işaretle"));
        Add(Tool.Note, "", L.T("Yapışkan not ekle"));
        Add(Tool.AddText, "", L.T("Metin ekle: tıkla ve yaz (Ctrl+Enter bitirir)"));
        Add(Tool.Correct, "", L.T("Metni düzelt: kelimeleri seç, yenisini yaz (Enter). Kayıtta altındaki eski metin silinir veya örtülür"));
        foreach (var s in new[] { "8", "9", "10", "11", "12", "14", "16", "18", "24", "32" }) _sizeBox.Items.Add(s);
        _sizeBox.Text = "11"; _sizeBox.Margin = new Thickness(6, 0, 0, 0); _sizeBox.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(_sizeBox);
        return panel;
    }

    UIElement BuildColorPicker()
    {
        foreach (var (name, hex) in Swatches)
        {
            var b = new Button { Width = 18, Height = 18, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), ToolTip = name, Tag = hex, Cursor = Cursors.Hand, Padding = new Thickness(0),
                Template = SwatchTemplate(), Background = Brush(hex) };
            b.Click += (_, _) => { if (_toolColor.ContainsKey(_tool)) { _toolColor[_tool] = hex; UpdateSwatches(); } else _status.Text = L.T("Renk, işaretleme araçları için geçerlidir."); };
            _swatchPanel.Children.Add(b);
        }
        return _swatchPanel;
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        f.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        t.VisualTree = f; return t;
    }

    void UpdateSwatches()
    {
        var cur = _toolColor.TryGetValue(_tool, out var c) ? c : null;
        foreach (Button b in _swatchPanel.Children)
        {
            var on = string.Equals(b.Tag as string, cur, StringComparison.OrdinalIgnoreCase);
            b.BorderThickness = new Thickness(on ? 2.5 : 1); b.BorderBrush = on ? ThemeManager.Brush("TextPrimaryBrush") : ThemeManager.Brush("BorderStrongBrush");
            b.Opacity = cur == null ? .45 : 1;
        }
    }

    UIElement BuildSearchBox()
    {
        var holder = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var hint = new TextBlock { Text = L.T("Belgede ara"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Foreground = ThemeManager.Brush("TextMutedBrush") };
        _searchBox.TextChanged += (_, _) => hint.Visibility = _searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; if (_hits.Count > 0 && _lastQuery == _searchBox.Text.Trim()) NextHit((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); else await SearchAsync(_searchBox.Text); }
            else if (e.Key == Key.Escape) { _searchBox.Clear(); ClearHits(); _scroll.Focus(); }
        };
        holder.Children.Add(_searchBox); holder.Children.Add(hint);
        return holder;
    }

    static SolidColorBrush Brush(string hex, byte? alpha = null)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex); if (alpha is { } a) c.A = a;
        var b = new SolidColorBrush(c); b.Freeze(); return b;
    }

    void SetTool(Tool t)
    {
        CommitInline();
        _tool = t;
        if (_toolButtons.TryGetValue(t, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
        foreach (var p in _pageStates) p.Overlay.Cursor = CursorFor(t);
        _scroll.Cursor = t == Tool.Hand ? Cursors.Hand : null;
        if (t != Tool.Object) { _selObject = null; _selEdit = null; if (_selA < 0) ClearSelectionVisuals(); }
        UpdateSwatches();
        _sizeBox.IsEnabled = t is Tool.AddText or Tool.Correct;
        if (t is Tool.Text or Tool.Highlight or Tool.Underline or Tool.Strike or Tool.Correct && _loaded) _ = LoadPageTextAsync(_page);
        if (t == Tool.Object && _loaded) _ = LoadObjectsAsync(_page);
    }

    static Cursor? CursorFor(Tool t) => t switch
    {
        Tool.Hand => Cursors.Hand, Tool.Text or Tool.Highlight or Tool.Underline or Tool.Strike or Tool.Correct => Cursors.IBeam,
        Tool.Object => Cursors.Arrow, Tool.Ink or Tool.Rect => Cursors.Cross, Tool.Note or Tool.AddText => Cursors.Pen, _ => null,
    };

    double CurrentSize => double.TryParse(_sizeBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 4, 96) : 11;

    // ------------------------------------------------------------------ loading words and objects

    async Task<List<PdfWord>> EnsureWordsAsync(PageState p)
    {
        if (p.Words != null) return p.Words;
        var r = await Call("pdfs_words", new { page = p.Index });
        p.Words = ParseWords(r, false);
        return p.Words;
    }

    static List<PdfWord> ParseWords(JsonElement r, bool ocr) => r.GetProperty("words").EnumerateArray()
        .Select(w => new PdfWord(w.GetProperty("x").GetDouble(), w.GetProperty("y").GetDouble(), w.GetProperty("w").GetDouble(), w.GetProperty("h").GetDouble(),
            w.GetProperty("text").GetString() ?? "", w.TryGetProperty("nl", out var nl) && nl.GetBoolean(), ocr)).ToList();

    async Task LoadObjectsAsync(int page)
    {
        if (page < 0 || page >= _pageStates.Count) return;
        var p = _pageStates[page]; if (p.Objects != null) return;
        try
        {
            var r = await Call("pdfs_objects", new { page });
            p.Objects = r.GetProperty("objects").EnumerateArray().Select(o => new PdfObject(o.GetProperty("index").GetInt32(), o.GetProperty("type").GetString() ?? "",
                o.GetProperty("x").GetDouble(), o.GetProperty("y").GetDouble(), o.GetProperty("w").GetDouble(), o.GetProperty("h").GetDouble())).ToList();
        }
        catch (Exception ex) { if (!_life.IsCancellationRequested) _status.Text = L.T("Nesneler okunamadı: ") + ex.Message; }
    }

    // ------------------------------------------------------------------ mouse: on the page

    bool IsWordTool => _tool is Tool.Text or Tool.Highlight or Tool.Underline or Tool.Strike or Tool.Correct;

    async void OnPageDown(PageState p, MouseButtonEventArgs e)
    {
        if (_tool == Tool.Hand) return;
        if (_inlineEditor != null && e.OriginalSource is DependencyObject src && IsInside(src, _inlineEditor)) return;
        CommitInline();
        var pos = e.GetPosition(p.Overlay);
        _page = p.Index; UpdatePageUi();
        _scroll.Focus();
        e.Handled = true;
        if (IsWordTool)
        {
            var words = p.Words;
            if (words == null) { _status.Text = L.T("Sayfa metni yükleniyor…"); words = await EnsureWordsAsync(p); }
            ClearSelection(keepHits: true);
            if (words.Count == 0 && !p.OcrTried)
            {
                p.OcrTried = true;
                _status.Text = L.F("Sayfa {0} metin katmanı içermiyor (çizim veya tarama). Metin tanıma otomatik başlatıldı…", p.Index + 1);
                await OcrPageAsync(p.Index);
                return;
            }
            if (words.Count == 0)
            {
                if (_tool is Tool.Highlight or Tool.Correct or Tool.Text)
                { _areaSel = new Rect(pos, pos); _selPage = p.Index; _dragStart = pos; _dragging = true; p.Overlay.CaptureMouse(); }
                if (_tool == Tool.Text) _status.Text = L.T("Bu sayfada metin katmanı yok. Alan seçebilir veya OCR ile metni tanıtabilirsiniz.");
                return;
            }
            var i = NearestWord(words, pos);
            _selPage = p.Index; _selA = _selB = i; _dragStart = pos;
            if (e.ClickCount >= 2) { DrawSelection(); FinishWordGesture(p); return; }
            _dragging = true; p.Overlay.CaptureMouse(); DrawSelection();
            return;
        }
        switch (_tool)
        {
            case Tool.Object:
                if (e.OriginalSource is FrameworkElement { Tag: PdfEdit pe } && pe.Kind is not ("object_move" or "object_delete"))
                { ClearSelection(true); _selEdit = pe; _selPage = p.Index; DrawSelection(); _selInfo.Text = L.T("Seçili: ") + EditName(pe); return; }
                if (p.Objects == null) { await LoadObjectsAsync(p.Index); }
                var obj = PickObject(p, pos);
                ClearSelection(true);
                if (obj == null) { _selInfo.Text = L.T("Burada seçilebilir nesne yok."); return; }
                _selObject = obj; _selPage = p.Index; _dragStart = pos; _dragging = true; p.Overlay.CaptureMouse(); DrawSelection();
                _selInfo.Text = $"{ObjectName(obj.Type)} • {obj.W * 25.4 / 72:0.#} × {obj.H * 25.4 / 72:0.#} mm";
                break;
            case Tool.Ink:
                _inkPoints = new() { pos }; _inkLine = new Polyline { Stroke = Brush(_toolColor[Tool.Ink]), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _inkLine.Points.Add(pos); p.Temp.Children.Add(_inkLine); _dragging = true; p.Overlay.CaptureMouse(); _selPage = p.Index;
                break;
            case Tool.Rect:
                _areaSel = new Rect(pos, pos); _dragStart = pos; _dragging = true; _selPage = p.Index; p.Overlay.CaptureMouse();
                break;
            case Tool.Note:
                if (e.OriginalSource is FrameworkElement { Tag: PdfEdit { Kind: "note" } existing }) { EditNote(p, existing); return; }
                OpenInline(p, new Rect(pos.X, pos.Y, 180, 60), "", 10, false, true, text =>
                {
                    if (text.Trim().Length == 0) return;
                    AddEdit(new PdfEdit { Page = p.Index, Kind = "note", At = pos, Text = text.Trim(), Color = _toolColor[Tool.Note] }, L.T("Not"));
                });
                break;
            case Tool.AddText:
                var size = CurrentSize;
                OpenInline(p, new Rect(pos.X, pos.Y, Math.Max(120, p.W - pos.X - 18), size * 1.3 + 4), "", size, false, true, text =>
                {
                    if (text.Trim().Length == 0) return;
                    var lines = text.TrimEnd().Split('\n');
                    var w = lines.Max(l => MeasureWidth(l, size)) + 6;
                    AddEdit(new PdfEdit { Page = p.Index, Kind = "text", Rect = new Rect(pos.X, pos.Y, w, lines.Length * size * 1.18 + 4), Text = text.TrimEnd().Replace("\r", ""), Size = size, Color = _toolColor[Tool.AddText] }, L.T("Metin ekleme"));
                });
                break;
        }
    }

    static bool IsInside(DependencyObject d, DependencyObject ancestor) { for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x == ancestor) return true; return false; }

    void OnPageMove(PageState p, MouseEventArgs e)
    {
        if (!_dragging || _selPage != p.Index) return;
        var pos = e.GetPosition(p.Overlay);
        if (_inkLine != null && _inkPoints != null)
        {
            if ((pos - _inkPoints[^1]).Length > .6) { _inkPoints.Add(pos); _inkLine.Points.Add(pos); }
            return;
        }
        if (_areaSel != null) { _areaSel = new Rect(_dragStart, pos); DrawTempRect(p, _areaSel.Value, _tool == Tool.Rect ? _toolColor[Tool.Rect] : null); return; }
        if (_selObject != null)
        {
            var d = pos - _dragStart;
            p.Temp.Children.Clear();
            if (d.Length < 2) return;
            var r = _selObject.Box; r.Offset(d.X, d.Y);
            if (Crop(p, _selObject.Box) is { } img) { Canvas.SetLeft(img, r.X); Canvas.SetTop(img, r.Y); img.Opacity = .75; p.Temp.Children.Add(img); }
            DrawTempRect(p, r, null, dashed: true);
            return;
        }
        if (IsWordTool && p.Words is { Count: > 0 } words)
        {
            _selB = NearestWord(words, pos); DrawSelection();
            if (Math.Abs(pos.Y - _dragStart.Y) > 1 || Math.Abs(pos.X - _dragStart.X) > 1) AutoScrollNear(e);
        }
    }

    void AutoScrollNear(MouseEventArgs e)
    {
        var y = e.GetPosition(_scroll).Y;
        if (y < 20) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 18);
        else if (y > _scroll.ViewportHeight - 20) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 18);
    }

    void OnPageUp(PageState p, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; p.Overlay.ReleaseMouseCapture(); e.Handled = true;
        var pos = e.GetPosition(p.Overlay);
        if (_inkLine != null && _inkPoints != null)
        {
            p.Temp.Children.Clear(); var pts = _inkPoints; _inkLine = null; _inkPoints = null;
            if (pts.Count > 1) AddEdit(new PdfEdit { Page = p.Index, Kind = "ink", Strokes = new() { Simplify(pts, .35) }, Color = _toolColor[Tool.Ink], Width = 1.6 }, L.T("Kalem"));
            return;
        }
        if (_areaSel is { } area)
        {
            p.Temp.Children.Clear(); _areaSel = null;
            if (area.Width < 3 || area.Height < 3) return;
            if (_tool == Tool.Rect) AddEdit(new PdfEdit { Page = p.Index, Kind = "rect", Rect = area, Color = _toolColor[Tool.Rect], Width = 1.6 }, L.T("Dikdörtgen"));
            else if (_tool == Tool.Highlight) AddEdit(new PdfEdit { Page = p.Index, Kind = "highlight", Rects = new() { area }, Color = _toolColor[Tool.Highlight] }, L.T("Alan vurgusu"));
            else if (_tool == Tool.Correct) StartCorrection(p, area, "", Math.Clamp(area.Height * .7, 5, 48), area.Bottom - area.Height * .22);
            else { _areaSel = area; _selPage = p.Index; DrawTempRect(p, area, null, dashed: true); _selInfo.Text = L.F("Alan {0:0.#} × {1:0.#} mm", area.Width * 25.4 / 72, area.Height * 25.4 / 72); }
            return;
        }
        if (_selObject != null)
        {
            p.Temp.Children.Clear();
            var d = pos - _dragStart;
            if (d.Length >= 2)
            {
                var o = _selObject;
                var edit = new PdfEdit { Page = p.Index, Kind = "object_move", Index = o.Index, Dx = d.X, Dy = d.Y, Rect = o.Box };
                AddEdit(edit, L.T("Nesne taşıma"));
                _selObject = o with { X = o.X + d.X, Y = o.Y + d.Y }; DrawSelection();
                _status.Text = L.T("Nesne taşındı (önizleme). Kayıtta PDF içindeki nesnenin kendisi taşınır.");
            }
            return;
        }
        if (IsWordTool) FinishWordGesture(p);
    }

    void FinishWordGesture(PageState p)
    {
        var words = SelectedWords(); if (words.Count == 0) return;
        UpdateSelectionInfo();
        switch (_tool)
        {
            case Tool.Highlight: AddEdit(new PdfEdit { Page = p.Index, Kind = "highlight", Rects = LineRects(words), Color = _toolColor[Tool.Highlight], Text = null }, L.T("Vurgu")); ClearSelection(true); break;
            case Tool.Underline: AddEdit(new PdfEdit { Page = p.Index, Kind = "underline", Rects = LineRects(words), Color = _toolColor[Tool.Underline] }, L.T("Altı çizili")); ClearSelection(true); break;
            case Tool.Strike: AddEdit(new PdfEdit { Page = p.Index, Kind = "strikeout", Rects = LineRects(words), Color = _toolColor[Tool.Strike] }, L.T("Üstü çizili")); ClearSelection(true); break;
            case Tool.Correct: CorrectSelection(); break;
        }
    }

    // ------------------------------------------------------------------ selection helpers

    static int NearestWord(List<PdfWord> words, Point p)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var dy = p.Y < w.Y ? w.Y - p.Y : p.Y > w.Y + w.H ? p.Y - w.Y - w.H : 0;
            var dx = p.X < w.X ? w.X - p.X : p.X > w.X + w.W ? p.X - w.X - w.W : 0;
            var d = dy * 4 + dx;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return best;
    }

    List<PdfWord> SelectedWords()
    {
        if (_selPage < 0 || _selA < 0 || _pageStates[_selPage].Words is not { } words || words.Count == 0) return new();
        var a = Math.Min(_selA, _selB); var b = Math.Max(_selA, _selB);
        return words.Skip(a).Take(b - a + 1).ToList();
    }

    static List<Rect> LineRects(List<PdfWord> words)
    {
        var rects = new List<Rect>();
        foreach (var w in words)
        {
            if (rects.Count > 0)
            {
                var last = rects[^1]; var mid = w.Y + w.H / 2;
                if (mid > last.Y && mid < last.Bottom && w.X >= last.X - 2 && w.X - last.Right < Math.Max(w.H, last.Height) * 3)
                { rects[^1] = Rect.Union(last, w.Box); continue; }
            }
            rects.Add(w.Box);
        }
        return rects;
    }

    static string WordsText(List<PdfWord> words)
    {
        var sb = new StringBuilder(); PdfWord? prev = null;
        foreach (var w in words)
        {
            if (prev != null)
            {
                var newLine = w.Nl || Math.Abs((w.Y + w.H / 2) - (prev.Y + prev.H / 2)) > Math.Max(w.H, prev.H) * .6;
                sb.Append(newLine ? "\n" : " ");
            }
            sb.Append(w.Text); prev = w;
        }
        return sb.ToString();
    }

    public string SelectedText => _areaSel is { } area && _selPage >= 0 && _pageStates[_selPage].Words is { } ws
        ? WordsText(ws.Where(w => area.IntersectsWith(w.Box)).ToList()) : WordsText(SelectedWords());

    void SelectAllOnPage(int page)
    {
        if (page < 0 || page >= _pageStates.Count || _pageStates[page].Words is not { Count: > 0 } w) { _status.Text = L.T("Bu sayfada seçilecek metin yok."); return; }
        ClearSelection(true); _selPage = page; _selA = 0; _selB = w.Count - 1; DrawSelection(); UpdateSelectionInfo();
    }

    void UpdateSelectionInfo()
    {
        var t = SelectedText;
        _selInfo.Text = t.Length == 0 ? "" : L.F("Seçili: {0} kelime", t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    void ClearSelection() => ClearSelection(false);
    void ClearSelection(bool keepHits)
    {
        _selA = _selB = -1; _areaSel = null; _selObject = null; _selEdit = null;
        ClearSelectionVisuals();
        if (!keepHits) ClearHits();
        _selInfo.Text = "";
    }

    void ClearSelectionVisuals() { foreach (var p in _pageStates) { p.Selection.Children.Clear(); p.Temp.Children.Clear(); } }

    void DrawSelection()
    {
        foreach (var p in _pageStates) p.Selection.Children.Clear();
        if (_selPage < 0) return;
        var page = _pageStates[_selPage];
        var accent = ThemeManager.Brush("AccentPrimaryBrush") as SolidColorBrush ?? Brushes.DodgerBlue;
        if (_selObject != null || _selEdit != null)
        {
            var r = _selObject?.Box ?? EditBounds(_selEdit!); r.Inflate(1.5, 1.5);
            page.Selection.Children.Add(MakeRect(r, null, accent, 1.2 / Z * 1.2, dashed: true));
            foreach (var c in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                page.Selection.Children.Add(MakeRect(new Rect(c.X - 2.5 / Z * 1.3, c.Y - 2.5 / Z * 1.3, 5 / Z * 1.3, 5 / Z * 1.3), Brushes.White, accent, 1 / Z));
            return;
        }
        var fill = new SolidColorBrush(Color.FromArgb(0x55, accent.Color.R, accent.Color.G, accent.Color.B)); fill.Freeze();
        foreach (var r in LineRects(SelectedWords())) page.Selection.Children.Add(MakeRect(Inflate(r, .6, .8), fill, null, 0));
    }

    static Rect Inflate(Rect r, double x, double y) { r.Inflate(x, y); return r; }

    static Rectangle MakeRect(Rect r, Brush? fill, Brush? stroke, double thickness, bool dashed = false)
    {
        var rect = new Rectangle { Width = Math.Max(0, r.Width), Height = Math.Max(0, r.Height), Fill = fill, Stroke = stroke, StrokeThickness = thickness, IsHitTestVisible = false };
        if (dashed) rect.StrokeDashArray = new DoubleCollection { 4, 3 };
        Canvas.SetLeft(rect, r.X); Canvas.SetTop(rect, r.Y); return rect;
    }

    void DrawTempRect(PageState p, Rect r, string? strokeHex, bool dashed = false)
    {
        if (!dashed) p.Temp.Children.Clear();
        var stroke = strokeHex != null ? Brush(strokeHex) : ThemeManager.Brush("AccentPrimaryBrush");
        var fill = _tool == Tool.Highlight ? Brush(_toolColor[Tool.Highlight], 0x66) : null;
        p.Temp.Children.Add(MakeRect(r, fill, stroke, 1.2 / Z * 1.3, dashed || strokeHex == null));
    }

    PdfObject? PickObject(PageState p, Point pos)
    {
        if (p.Objects == null) return null;
        var area = p.W * p.H;
        var candidates = p.Objects.Where(o => Inflate(o.Box, 2, 2).Contains(pos)).OrderBy(o => o.W * o.H).ToList();
        return candidates.FirstOrDefault(o => o.W * o.H < area * .9) ?? candidates.FirstOrDefault();
    }

    static string ObjectName(string type) => type switch { "text" => L.T("Metin nesnesi"), "image" => L.T("Görsel"), "path" => L.T("Çizim / çizgi"), "form" => "Form XObject", "shading" => L.T("Gölgeleme"), _ => L.T("Nesne") };

    Image? Crop(PageState p, Rect r)
    {
        if (p.Image.Source is not BitmapSource src || p.RenderedScale <= 0) return null;
        var s = p.RenderedScale;
        var x = (int)Math.Max(0, Math.Floor(r.X * s)); var y = (int)Math.Max(0, Math.Floor(r.Y * s));
        var w = (int)Math.Min(src.PixelWidth - x, Math.Ceiling(r.Width * s)); var h = (int)Math.Min(src.PixelHeight - y, Math.Ceiling(r.Height * s));
        if (w <= 0 || h <= 0) return null;
        try { var c = new CroppedBitmap(src, new Int32Rect(x, y, w, h)); c.Freeze(); return new Image { Source = c, Width = w / s, Height = h / s, Stretch = Stretch.Fill, IsHitTestVisible = false }; }
        catch { return null; }
    }

    static List<Point> Simplify(List<Point> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count]; keep[0] = keep[^1] = true;
        void Rdp(int a, int b)
        {
            if (b <= a + 1) return; double maxD = 0; int idx = -1; var A = pts[a]; var B = pts[b]; var ab = B - A; var len = ab.Length;
            for (int i = a + 1; i < b; i++)
            {
                var d = len < 1e-9 ? (pts[i] - A).Length : Math.Abs(Vector.CrossProduct(ab, pts[i] - A)) / len;
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (maxD > tol) { keep[idx] = true; Rdp(a, idx); Rdp(idx, b); }
        }
        Rdp(0, pts.Count - 1);
        return pts.Where((_, i) => keep[i]).ToList();
    }

    static double MeasureWidth(string text, double size)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Arial"), size, Brushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    // ------------------------------------------------------------------ correction and inline editor

    void CorrectSelection()
    {
        var words = SelectedWords();
        if (_selPage < 0 || words.Count == 0) { _status.Text = L.T("Düzeltmek için önce metin seçin."); return; }
        var p = _pageStates[_selPage];
        var lines = LineRects(words);
        if (lines.Count > 1) { _status.Text = L.T("Düzeltme tek satır içinde yapılır; ilk satır alındı."); words = words.Where(w => w.Y + w.H / 2 >= lines[0].Y && w.Y + w.H / 2 <= lines[0].Bottom).ToList(); }
        var box = LineRects(words)[0];
        var hs = words.Select(w => w.H).OrderBy(h => h).ToList(); var medianH = hs[hs.Count / 2];
        var size = Math.Clamp(Math.Round(medianH / .78 * 2) / 2, 4, 96);
        var baseline = words.Min(w => w.Y + w.H);
        var cover = new Rect(box.X - .8, baseline - size * .93, Math.Max(box.Width + 1.6, 4), size * 1.18);
        cover = Rect.Union(cover, Inflate(box, .8, .6));
        StartCorrection(p, cover, WordsText(words), size, baseline);
    }

    void StartCorrection(PageState p, Rect cover, string original, double size, double baseline)
    {
        ClearSelection(true);
        _sizeBox.Text = size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        var editRect = new Rect(cover.X, cover.Y, Math.Max(cover.Width, MeasureWidth(original, size) + 30), cover.Height);
        OpenInline(p, editRect, original, size, true, false, text =>
        {
            if (text == original && original.Length > 0) return;
            var sz = CurrentSize;
            var w = Math.Max(cover.Width, MeasureWidth(text, sz) + 1.6);
            AddEdit(new PdfEdit { Page = p.Index, Kind = "correct", Rect = new Rect(cover.X, cover.Y, w, cover.Height), Text = text, Size = sz, Baseline = baseline, Color = _toolColor[Tool.Correct] }, L.T("Metin düzeltme"));
            _status.Text = L.T("Düzeltme eklendi. Kayıtta tamamen kutu içinde kalan eski metin nesneleri silinir, diğerleri beyazla örtülür.");
        }, baseline);
    }

    void OpenInline(PageState p, Rect r, string text, double size, bool opaque, bool multiline, Action<string> commit, double? baseline = null)
    {
        CommitInline();
        var tb = new TextBox
        {
            Text = text, FontFamily = new FontFamily("Arial"), FontSize = size, Padding = new Thickness(0), BorderThickness = new Thickness(1 / Z),
            BorderBrush = ThemeManager.Brush("AccentPrimaryBrush"), Background = opaque ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xEE, 255, 255, 255)),
            Foreground = Brush(_toolColor.TryGetValue(_tool, out var c) ? c : "#000000"), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinWidth = Math.Max(40, r.Width), MinHeight = r.Height, MaxWidth = Math.Max(r.Width, p.W - r.X), CaretBrush = Brushes.Black, Style = null,
        };
        if (_tool == Tool.Note) { tb.FontFamily = new FontFamily("Segoe UI"); tb.Background = Brush("#FFF4CE"); tb.Foreground = Brushes.Black; tb.MinHeight = 50; }
        Canvas.SetLeft(tb, r.X); Canvas.SetTop(tb, baseline is { } b ? b - size * .905 - 1 / Z : r.Y);
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelInline(); e.Handled = true; }
            else if (e.Key == Key.Enter && (!multiline || (Keyboard.Modifiers & ModifierKeys.Control) != 0)) { CommitInline(); e.Handled = true; }
        };
        tb.LostKeyboardFocus += (_, e) => { if (e.NewFocus is DependencyObject n && IsInside(n, tb)) return; Dispatcher.BeginInvoke(new Action(CommitInline)); };
        p.Overlay.Children.Add(tb);
        _inlineEditor = tb; _inlineCommit = commit;
        tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        _status.Text = multiline ? L.T("Yazın; Ctrl+Enter ile bitirin, Esc ile vazgeçin.") : L.T("Yeni metni yazın; Enter ile uygulayın, Esc ile vazgeçin.");
    }

    void CommitInline()
    {
        if (_inlineEditor is not TextBox tb) return;
        var commit = _inlineCommit; var text = tb.Text.Replace("\r\n", "\n");
        _inlineEditor = null; _inlineCommit = null;
        (tb.Parent as Panel)?.Children.Remove(tb);
        commit?.Invoke(text);
        _scroll.Focus();
    }

    void CancelInline()
    {
        if (_inlineEditor is not TextBox tb) return;
        _inlineEditor = null; _inlineCommit = null; (tb.Parent as Panel)?.Children.Remove(tb); _status.Text = L.T("Vazgeçildi."); _scroll.Focus();
    }

    void EditNote(PageState p, PdfEdit note)
    {
        var old = note.Text ?? "";
        OpenInline(p, new Rect(note.At.X, note.At.Y, 180, 60), old, 10, false, true, text =>
        {
            if (text.Trim() == old) return;
            void Set(string v) { note.Text = v; RedrawMarks(_pageStates[note.Page]); MarkDirty(); }
            Set(text.Trim()); PushUndo(() => Set(old), () => Set(text.Trim()), L.T("Not düzenleme"));
        });
    }

    // ------------------------------------------------------------------ edit list, drawing, undo

    void AddEdit(PdfEdit e, string name)
    {
        void Add() { _edits.Add(e); RedrawMarks(_pageStates[e.Page]); MarkDirty(); RefreshNotes(); }
        void Remove() { _edits.Remove(e); if (_selEdit == e) ClearSelection(true); RedrawMarks(_pageStates[e.Page]); MarkDirty(); RefreshNotes(); }
        Add(); PushUndo(Remove, Add, name);
        _status.Text = L.F("{0} eklendi. Kaydetmek için Farklı kaydet (Ctrl+S).", name);
    }

    void RemoveEdit(PdfEdit e, string name)
    {
        var idx = _edits.IndexOf(e); if (idx < 0) return;
        void Remove() { _edits.Remove(e); RedrawMarks(_pageStates[e.Page]); MarkDirty(); RefreshNotes(); }
        void Add() { _edits.Insert(Math.Min(idx, _edits.Count), e); RedrawMarks(_pageStates[e.Page]); MarkDirty(); RefreshNotes(); }
        Remove(); PushUndo(Add, Remove, name);
    }

    void PushUndo(Action undo, Action redo, string name) { _undo.Push(new(undo, redo, name)); _redo.Clear(); }
    void Undo() { CommitInline(); if (_undo.Count == 0) { _status.Text = L.T("Geri alınacak işlem yok."); return; } var op = _undo.Pop(); op.Undo(); _redo.Push(op); _status.Text = L.F("Geri alındı: {0}", op.Name); ClearSelection(true); }
    void Redo() { if (_redo.Count == 0) { _status.Text = L.T("Yinelenecek işlem yok."); return; } var op = _redo.Pop(); op.Redo(); _undo.Push(op); _status.Text = L.T("Yinelendi: ") + op.Name; }
    void MarkDirty() { _dirty = _edits.Count > 0 || _pageStates.Any(p => p.UserRotation != 0); Title = (_dirty ? "● " : "") + _titleBase; }

    void DeleteSelectedObject()
    {
        if (_selEdit != null) { var e = _selEdit; ClearSelection(true); RemoveEdit(e, L.T("Silme: ") + EditName(e)); return; }
        if (_selObject is { } o && _selPage >= 0)
        {
            var moved = _edits.FirstOrDefault(x => x.Kind == "object_move" && x.Page == _selPage && x.Index == o.Index);
            var orig = moved != null ? moved.Rect : o.Box;
            AddEdit(new PdfEdit { Page = _selPage, Kind = "object_delete", Index = o.Index, Rect = Rect.Union(orig, o.Box) }, L.T("Nesne silme"));
            _pageStates[_selPage].Objects?.Remove(_pageStates[_selPage].Objects!.First(x => x.Index == o.Index));
            ClearSelection(true);
            _status.Text = L.T("Nesne silindi (önizleme). Kayıtta PDF içindeki nesnenin kendisi kaldırılır.");
            return;
        }
        _status.Text = L.T("Silmek için Nesne seç aracıyla bir nesne veya not seçin.");
    }

    static string EditName(PdfEdit e) => e.Kind switch
    {
        "highlight" => L.T("Vurgu"), "underline" => L.T("Altı çizili"), "strikeout" => L.T("Üstü çizili"), "ink" => L.T("Kalem"), "rect" => L.T("Dikdörtgen"), "note" => L.T("Not"),
        "text" => L.T("Eklenen metin"), "correct" => L.T("Düzeltme"), "object_move" => L.T("Nesne taşıma"), "object_delete" => L.T("Nesne silme"), _ => e.Kind,
    };

    static Rect EditBounds(PdfEdit e) => e.Kind switch
    {
        "highlight" or "underline" or "strikeout" => e.Rects!.Aggregate(Rect.Union),
        "ink" => e.Strokes!.SelectMany(s => s).Aggregate(new Rect(e.Strokes![0][0], e.Strokes[0][0]), (r, p) => Rect.Union(r, p)),
        "note" => new Rect(e.At.X, e.At.Y, 18, 18),
        "object_move" => new Rect(e.Rect.X + e.Dx, e.Rect.Y + e.Dy, e.Rect.Width, e.Rect.Height),
        _ => e.Rect,
    };

    void RedrawMarks(PageState p)
    {
        p.Marks.Children.Clear();
        foreach (var e in _edits.Where(x => x.Page == p.Index))
            foreach (var el in Visuals(p, e)) { if (el is FrameworkElement fe) fe.Tag = e; p.Marks.Children.Add(el); }
    }

    IEnumerable<UIElement> Visuals(PageState p, PdfEdit e)
    {
        var color = Brush(e.Color);
        switch (e.Kind)
        {
            case "highlight":
                foreach (var r in e.Rects!) { var h = MakeRect(r, Brush(e.Color, 0x6E), null, 0); h.IsHitTestVisible = true; yield return h; }
                break;
            case "underline": case "strikeout":
                foreach (var r in e.Rects!)
                {
                    var y = e.Kind == "underline" ? r.Bottom - r.Height * .04 : r.Y + r.Height * .55;
                    yield return new Line { X1 = r.X, X2 = r.Right, Y1 = y, Y2 = y, Stroke = color, StrokeThickness = Math.Max(.8, r.Height * .07) };
                    yield return MakeRect(r, Brushes.Transparent, null, 0).Also(x => x.IsHitTestVisible = true);
                }
                break;
            case "ink":
                foreach (var s in e.Strokes!)
                {
                    var pl = new Polyline { Stroke = color, StrokeThickness = e.Width, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    foreach (var pt in s) pl.Points.Add(pt);
                    yield return pl;
                }
                break;
            case "rect":
                yield return MakeRect(e.Rect, Brushes.Transparent, color, e.Width).Also(x => x.IsHitTestVisible = true);
                break;
            case "note":
                var b = new Border { Width = 18, Height = 18, Background = color, CornerRadius = new CornerRadius(2), BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(.5),
                    Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black, IsHitTestVisible = false },
                    ToolTip = e.Text, Cursor = Cursors.Hand };
                Canvas.SetLeft(b, e.At.X); Canvas.SetTop(b, e.At.Y); yield return b;
                break;
            case "text": case "correct":
                if (e.Kind == "correct") yield return MakeRect(Inflate(e.Rect, .6, .6), Brushes.White, null, 0).Also(x => x.IsHitTestVisible = true);
                var lines = (e.Text ?? "").Split('\n');
                var firstBaseline = e.Baseline ?? (e.Rect.Y + 2 + e.Size * .92);
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = new TextBlock { Text = lines[i], FontFamily = new FontFamily("Arial"), FontSize = e.Size, Foreground = color, IsHitTestVisible = true }; Services.UiTranslator.SetSkip(t, true);
                    TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
                    Canvas.SetLeft(t, e.Rect.X + (e.Kind == "correct" ? .8 : 2)); Canvas.SetTop(t, firstBaseline + i * e.Size * 1.18 - e.Size * .905);
                    yield return t;
                }
                break;
            case "object_move":
                yield return MakeRect(Inflate(e.Rect, .5, .5), Brushes.White, null, 0);
                if (Crop(p, e.Rect) is { } img) { Canvas.SetLeft(img, e.Rect.X + e.Dx); Canvas.SetTop(img, e.Rect.Y + e.Dy); yield return img; }
                else yield return MakeRect(new Rect(e.Rect.X + e.Dx, e.Rect.Y + e.Dy, e.Rect.Width, e.Rect.Height), null, Brushes.Gray, .8, dashed: true);
                break;
            case "object_delete":
                yield return MakeRect(Inflate(e.Rect, .5, .5), Brushes.White, null, 0);
                break;
        }
    }

    // ------------------------------------------------------------------ context menu, copying

    void OnPageContext(PageState p, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu();
        var hasText = _selPage == p.Index && SelectedText.Length > 0;
        if (hasText)
        {
            menu.Items.Add(Menu(L.T("Kopyala"), "", CopySelection));
            menu.Items.Add(Menu(L.T("Vurgula"), "", () => { var prev = _tool; _tool = Tool.Highlight; FinishWordGesture(p); _tool = prev; }));
            menu.Items.Add(Menu(L.T("Altını çiz"), "", () => { var prev = _tool; _tool = Tool.Underline; FinishWordGesture(p); _tool = prev; }));
            menu.Items.Add(Menu(L.T("Üstünü çiz"), "", () => { var prev = _tool; _tool = Tool.Strike; FinishWordGesture(p); _tool = prev; }));
            menu.Items.Add(Menu(L.T("Metni düzelt…"), "", () => { var prev = _tool; _tool = Tool.Correct; CorrectSelection(); _tool = prev; }));
            menu.Items.Add(Menu(L.T("Seçimi yapay zekaya sor"), "", () => { _scopeSelection.IsChecked = true; _sideCol.Width = new GridLength(340); _question.Focus(); }));
            menu.Items.Add(Menu(L.T("Belgede ara"), "", () => { _searchBox.Text = SelectedText.Split('\n')[0]; _ = SearchAsync(_searchBox.Text); }));
            menu.Items.Add(new Separator());
        }
        if (_selObject is { Type: "image" } img && _selPage == p.Index) menu.Items.Add(Menu(L.T("Görseli kopyala"), "", async () => await CopyImageAsync(p.Index, img.Index)));
        if ((_selObject != null || _selEdit != null) && _selPage == p.Index) { menu.Items.Add(Menu(L.T("Sil"), "", DeleteSelectedObject)); menu.Items.Add(new Separator()); }
        menu.Items.Add(Menu(L.T("Bu sayfada tümünü seç"), "", () => SelectAllOnPage(p.Index)));
        menu.Items.Add(Menu(L.T("Bu sayfada OCR (metin tanı)"), "", async () => await OcrPageAsync(p.Index)));
        menu.Items.Add(Menu(L.T("Sayfayı sağa döndür"), "", () => RotatePages(new[] { p.Index }, 90)));
        menu.Items.Add(Menu(L.T("Sayfayı sola döndür"), "", () => RotatePages(new[] { p.Index }, -90)));
        menu.PlacementTarget = p.Overlay; menu.IsOpen = true;
    }

    void CopySelection()
    {
        var t = SelectedText;
        if (t.Length == 0) { _status.Text = L.T("Kopyalanacak seçim yok."); return; }
        try { Clipboard.SetText(t); _status.Text = L.F("{0} karakter panoya kopyalandı.", t.Length) + (_pageStates[_selPage].WordsFromOcr ? L.T(" (OCR — sayıları kaynakta doğrulayın)") : ""); }
        catch (Exception ex) { _status.Text = L.T("Panoya yazılamadı: ") + ex.Message; }
    }

    async Task CopyImageAsync(int page, int index)
    {
        try
        {
            var r = await Call("pdfs_image", new { page, index });
            var bmp = await Task.Run(() => Decode(r.GetProperty("image").GetString()!));
            Clipboard.SetImage(bmp); _status.Text = L.F("Görsel panoya kopyalandı ({0}×{1} piksel).", bmp.PixelWidth, bmp.PixelHeight);
        }
        catch (Exception ex) { _status.Text = L.T("Görsel alınamadı: ") + ex.Message; }
    }

    // ------------------------------------------------------------------ arama

    string _lastQuery = "";
    async Task SearchAsync(string query)
    {
        query = query.Trim(); ClearHits(); _lastQuery = query;
        if (query.Length == 0) return;
        _status.Text = L.T("Aranıyor…");
        try
        {
            var r = await Call("pdfs_search", new { query });
            foreach (var h in r.GetProperty("hits").EnumerateArray())
                _hits.Add((h.GetProperty("page").GetInt32(), h.GetProperty("rects").EnumerateArray().Select(x => new Rect(x.GetProperty("x").GetDouble(), x.GetProperty("y").GetDouble(), x.GetProperty("w").GetDouble(), x.GetProperty("h").GetDouble())).ToList()));
            if (_hits.Count == 0) { _status.Text = L.F("\"{0}\" bulunamadı", query) + (_pageStates.Any(p => p.WordsFromOcr) ? "." : L.T(". Taranmış sayfalarda önce OCR çalıştırın.")); return; }
            foreach (var g in _hits.GroupBy(h => h.Page))
            {
                var layer = HitLayer(g.Key);
                foreach (var hit in g) foreach (var rect in hit.Rects) layer.Children.Add(MakeRect(Inflate(rect, .8, .8), Brush("#FF8C00", 0x55), null, 0));
            }
            _hitIndex = -1; NextHit(1);
        }
        catch (Exception ex) { _status.Text = L.T("Arama yapılamadı: ") + ex.Message; }
    }

    Canvas HitLayer(int page)
    {
        if (_hitLayers.TryGetValue(page, out var c)) return c;
        c = new Canvas { IsHitTestVisible = false }; _pageStates[page].Overlay.Children.Insert(1, c); _hitLayers[page] = c; return c;
    }

    void ClearHits() { foreach (var c in _hitLayers.Values) c.Children.Clear(); _hits.Clear(); _hitIndex = -1; }

    void NextHit(int dir)
    {
        if (_hits.Count == 0) { if (_searchBox.Text.Trim().Length > 0) _ = SearchAsync(_searchBox.Text); return; }
        _hitIndex = ((_hitIndex + dir) % _hits.Count + _hits.Count) % _hits.Count;
        var (page, rects) = _hits[_hitIndex];
        RecomputeTops();
        var p = _pageStates[page]; var r = rects[0];
        var local = p.Overlay.TranslatePoint(new Point(r.X, r.Y), _stack);
        _scroll.ScrollToVerticalOffset(Math.Max(0, local.Y + _stack.Margin.Top * 0 - _scroll.ViewportHeight / 3));
        var x = local.X - _scroll.ViewportWidth / 2; if (x > 0 || _scroll.HorizontalOffset > 0) _scroll.ScrollToHorizontalOffset(Math.Max(0, x));
        var layer = HitLayer(page);
        foreach (var other in _hitLayers.Values) foreach (var el in other.Children.OfType<Rectangle>()) el.Stroke = null;
        foreach (var el in layer.Children.OfType<Rectangle>().Where(el => Math.Abs(Canvas.GetLeft(el) - (r.X - .8)) < .01 && Math.Abs(Canvas.GetTop(el) - (r.Y - .8)) < .01)) { el.Stroke = Brush("#C42B1C"); el.StrokeThickness = 1.2; }
        _status.Text = L.F("\"{0}\": {1} / {2} (sayfa {3}). Enter / F3 sonraki, Shift ile önceki.", _lastQuery, _hitIndex + 1, _hits.Count, page + 1);
    }

    // ------------------------------------------------------------------ kaydetme

    async Task<bool> SaveAsAsync()
    {
        CommitInline();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Düzenlenmiş PDF'yi kaydet"), Filter = L.T("PDF belgesi (*.pdf)|*.pdf"), DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(_sourceDocument ?? _path) + (_sourceDocument != null ? ".pdf" : "_duzenlenmis.pdf"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return false;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, L.T("Ek dosyasının üzerine yazılmaz; farklı bir ad seçin."), "MailStudio"); return false; }
        var ok = await SaveToAsync(dlg.FileName);
        if (ok && MessageBox.Show(this, L.T("PDF kaydedildi:\n") + dlg.FileName + L.T("\n\nKlasörde gösterilsin mi?"), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true });
        return ok;
    }

    /// <summary>Writes edits and rotations to a new PDF; the source attachment does not change.</summary>
    async Task<bool> SaveToAsync(string dest)
    {
        CommitInline();
        _status.Text = L.T("Kaydediliyor…");
        try
        {
            var rotations = _pageStates.Where(p => p.UserRotation != 0).ToDictionary(p => p.Index.ToString(), p => p.Rotation);
            var r = await Call("pdfs_save", new { source = _path, dest, password = _password, rotations, items = _edits.Select(e => e.ToWorker()).ToArray() });
            var st = r.GetProperty("stats");
            int S(string k) => st.TryGetProperty(k, out var v) ? v.GetInt32() : 0;
            _dirty = false; Title = _titleBase;
            _status.Text = L.F("Kaydedildi: {0} — {1} işaret, {2} metin, {3} taşıma, {4} silme", dest, S("annotations"), S("texts"), S("moved"), S("deleted")) +
                (S("removed_text_objects") > 0 ? L.F(", eski metinden {0} nesne kaldırıldı", S("removed_text_objects")) : "") + (S("covered") > 0 ? L.F(", {0} alan örtüldü", S("covered")) : "") + ".";
            return true;
        }
        catch (Exception ex) { _status.Text = L.T("Kaydedilemedi: ") + ex.Message; if (IsVisible) MessageBox.Show(this, L.T("PDF kaydedilemedi:\n") + ex.Message, "MailStudio", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    bool _closeAfterSave;
    async void OnClosingAsk(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty || _closeAfterSave) return;
        var answer = MessageBox.Show(this, L.T("Kaydedilmemiş PDF düzenlemeleri var. Kapatmadan önce kaydedilsin mi?"), "MailStudio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.No) return;
        e.Cancel = true;
        if (answer == MessageBoxResult.Cancel) return;
        if (await SaveAsAsync()) { _closeAfterSave = true; Close(); }
    }
}

public static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}

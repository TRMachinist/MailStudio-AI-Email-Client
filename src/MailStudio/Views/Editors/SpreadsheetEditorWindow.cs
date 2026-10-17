using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClosedXML.Excel;
using MailStudio.Services;

namespace MailStudio.Views.Editors;

/// <summary>
/// Excel editor (ClosedXML, MIT): sheet tabs, formula bar, cell/range selection, value and formula input,
/// bold/italic/underline, font and fill color, alignment, insert/delete rows and columns, Σ sum, copy/paste, undo.
/// Cells whose formatting does not change are kept as in the file. The attachment itself is never written; the first save is Save as.
/// </summary>
public sealed class SpreadsheetEditorWindow : Window
{
    readonly string _source; string? _savePath; XLWorkbook _wb; IXLWorksheet _ws;
    readonly SheetView _view = new();
    readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical }, _hbar = new() { Orientation = Orientation.Horizontal };
    readonly TextBox _nameBox = new() { Width = 80, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    readonly TextBox _formula = new() { VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _stats = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly Canvas _overlay = new();
    readonly Dictionary<long, CellView> _cache = new();
    readonly List<(int, int, int, int)> _merges = new();
    readonly Dictionary<int, double> _colW = new(), _rowH = new();
    readonly Stack<Op> _undo = new(), _redo = new();
    TextBox? _editor; bool _dirty;
    public bool IsDirty => _dirty;

    public SpreadsheetEditorWindow(string path, string? displaySource = null)
    {
        _source = displaySource ?? path;
        _wb = new XLWorkbook(path);
        _ws = _wb.Worksheets.First();
        Title = Path.GetFileName(_source) + L.T(" — Excel düzenleyici");
        Width = 1280; Height = 840; MinWidth = 720; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid();
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = h });
        Content = root;
        root.Children.Add(BuildToolbar());

        var fbar = new DockPanel { Margin = new Thickness(6, 4, 6, 4) }; Grid.SetRow(fbar, 1); root.Children.Add(fbar);
        DockPanel.SetDock(_nameBox, Dock.Left); fbar.Children.Add(_nameBox);
        var fx = new TextBlock { Text = "fx", FontStyle = FontStyles.Italic, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("TextSecondaryBrush") };
        DockPanel.SetDock(fx, Dock.Left); fbar.Children.Add(fx); fbar.Children.Add(_formula);
        _formula.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitInput(_view.ActiveRow, _view.ActiveCol, _formula.Text); _view.Select(_view.ActiveRow + 1, _view.ActiveCol, false); _view.Focus(); e.Handled = true; } else if (e.Key == Key.Escape) { ShowActive(); _view.Focus(); } };
        _nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { GoTo(_nameBox.Text); _view.Focus(); e.Handled = true; } };

        var area = new Grid(); Grid.SetRow(area, 2); root.Children.Add(area);
        area.ColumnDefinitions.Add(new()); area.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        area.RowDefinitions.Add(new()); area.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var host = new Grid(); host.Children.Add(_view); host.Children.Add(_overlay); area.Children.Add(host);
        Grid.SetColumn(_vbar, 1); area.Children.Add(_vbar); Grid.SetRow(_hbar, 1); area.Children.Add(_hbar);
        _vbar.Scroll += (_, _) => { _view.OffsetY = _vbar.Value; _view.InvalidateVisual(); PlaceEditor(); };
        _hbar.Scroll += (_, _) => { _view.OffsetX = _hbar.Value; _view.InvalidateVisual(); PlaceEditor(); };
        _view.SizeChanged += (_, _) => SyncBars();
        _view.ViewChanged += () => { SyncBars(); PlaceEditor(); };
        _view.SelectionChanged += () => { CommitEditor(); ShowActive(); };
        _view.EditRequested += startEmpty => BeginEdit(startEmpty ? "" : null);
        _view.ColumnResized += (c, w) => { _ws.Column(c).Width = Math.Max(.5, (w - 5) / 7.0); _colW[c] = w; _view.Rebuild(); MarkDirty(); };
        _view.GetCell = (r, c) => _cache.TryGetValue(CK(r, c), out var v) ? v : null;
        _view.ColWidth = c => _colW.TryGetValue(c, out var w) ? w : DefaultColW;
        _view.RowHeight = r => _rowH.TryGetValue(r, out var h) ? h : DefaultRowH;
        _view.Merges = () => _merges;
        _view.PreviewKeyDown += OnGridKey;
        _view.TextInput += (_, e) => { if (_editor == null && e.Text.Length > 0 && !char.IsControl(e.Text[0])) { BeginEdit(e.Text); e.Handled = true; } };

        var tabBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), Padding = new Thickness(6, 2, 6, 2) };
        var tabDock = new DockPanel(); tabBar.Child = tabDock;
        var add = new Button { Content = new TextBlock { Text = "\uE710", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 }, ToolTip = L.T("Yeni sayfa"), Width = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0) }; add.Click += (_, _) => AddSheet();
        DockPanel.SetDock(add, Dock.Left); tabDock.Children.Add(add);
        tabDock.Children.Add(new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(tabBar, 3); root.Children.Add(tabBar);

        var status = new Border { Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var sd = new DockPanel(); status.Child = sd; DockPanel.SetDock(_stats, Dock.Right); _stats.Foreground = ThemeManager.Brush("TextSecondaryBrush");
        sd.Children.Add(_stats); sd.Children.Add(_status);
        Grid.SetRow(status, 4); root.Children.Add(status);

        PreviewKeyDown += (_, e) =>
        {
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (ctrl && e.Key == Key.S) { _ = SaveAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
        };
        Closing += OnClosing;
        LoadSheet();
        Loaded += (_, _) => { _view.Focus(); _status.Text = L.T("Hücreyi seçip yazın; = ile formül. Enter/Tab ilerler, F2 düzenler, Delete temizler. Ek dosyası değişmez; Kaydet ilk seferde yeni dosya sorar."); };
    }

    const double DefaultColW = 72, DefaultRowH = 20;
    static long CK(int r, int c) => ((long)r << 15) | (uint)c;

    // ------------------------------------------------------------------ toolbar

    UIElement BuildToolbar()
    {
        var bar = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var w = new WrapPanel(); bar.Child = w;
        Button B(string glyph, string tip, Action a, string? text = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = sp, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(7, 3, 7, 3), MinHeight = 28 };
            if (TryFindResource("SubtleButton") is Style s) b.Style = s;
            b.Click += (_, _) => { CommitEditor(); a(); _view.Focus(); }; w.Children.Add(b); return b;
        }
        Border Sep() { var s = new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), Background = ThemeManager.Brush("BorderSubtleBrush"), VerticalAlignment = VerticalAlignment.Center }; w.Children.Add(s); return s; }
        B("", "Kaydet (Ctrl+S)", () => _ = SaveAsync(false), L.T("Kaydet"));
        B("", L.T("Farklı kaydet (Ctrl+Shift+S)"), () => _ = SaveAsync(true));
        Sep();
        B("", L.T("Geri al (Ctrl+Z)"), Undo); B("", "Yinele (Ctrl+Y)", Redo);
        Sep();
        B("", L.T("Kalın (Ctrl+B)"), () => Format(L.T("Kalın"), s => s.Font.Bold = !Active().Style.Font.Bold));
        B("", L.T("İtalik (Ctrl+I)"), () => Format(L.T("İtalik"), s => s.Font.Italic = !Active().Style.Font.Italic));
        B("", L.T("Altı çizili (Ctrl+U)"), () => Format(L.T("Altı çizili"), s => s.Font.Underline = Active().Style.Font.Underline == XLFontUnderlineValues.None ? XLFontUnderlineValues.Single : XLFontUnderlineValues.None));
        ColorMenu(B("", L.T("Yazı rengi"), () => { }), true);
        ColorMenu(B("", L.T("Dolgu rengi"), () => { }), false);
        Sep();
        B("", L.T("Sola hizala"), () => Format(L.T("Hizalama"), s => s.Alignment.Horizontal = XLAlignmentHorizontalValues.Left));
        B("", L.T("Ortala"), () => Format(L.T("Hizalama"), s => s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center));
        B("", L.T("Sağa hizala"), () => Format(L.T("Hizalama"), s => s.Alignment.Horizontal = XLAlignmentHorizontalValues.Right));
        B("", L.T("Metni kaydır"), () => Format(L.T("Kaydırma"), s => s.Alignment.WrapText = !Active().Style.Alignment.WrapText));
        B("", L.T("Hücreleri birleştir / ayır"), MergeToggle);
        Sep();
        B("", L.T("Üste satır ekle"), () => Structure(L.T("Satır ekle"), () => _ws.Row(_view.Selection.r1).InsertRowsAbove(_view.Selection.r2 - _view.Selection.r1 + 1)), L.T("Satır"));
        B("", L.T("Seçili satırları sil"), () => Structure(L.T("Satır sil"), () => _ws.Rows(_view.Selection.r1, Math.Min(_view.Selection.r2, LastRow())).Delete()));
        B("", L.T("Sola sütun ekle"), () => Structure(L.T("Sütun ekle"), () => _ws.Column(_view.Selection.c1).InsertColumnsBefore(_view.Selection.c2 - _view.Selection.c1 + 1)), L.T("Sütun"));
        B("", L.T("Seçili sütunları sil"), () => Structure(L.T("Sütun sil"), () => _ws.Columns(_view.Selection.c1, Math.Min(_view.Selection.c2, LastCol())).Delete()));
        Sep();
        B("", L.T("Otomatik toplam (seçimin altına veya üstteki sayıların toplamı)"), AutoSum, "Σ");
        B("", L.T("Sırala (A→Z, seçili sütuna göre; ilk satır başlık kabul edilir)"), () => SortSelection(true));
        B("", L.T("Sırala (Z→A)"), () => SortSelection(false));
        Sep();
        B("", L.T("Excel'de aç (tam özellikli düzenleme)"), () => Shell(_savePath ?? _source), L.T("Excel'de aç"));
        return bar;
    }

    void ColorMenu(Button b, bool fore)
    {
        var m = new ContextMenu();
        foreach (var (name, hex) in new[] { ("Otomatik / yok", ""), (L.T("Kırmızı"), "#C00000"), (L.T("Turuncu"), "#ED7D31"), (L.T("Sarı"), "#FFFF00"), (L.T("Açık sarı"), "#FFF2CC"), (L.T("Yeşil"), "#00B050"), (L.T("Açık yeşil"), "#E2EFDA"), (L.T("Mavi"), "#0070C0"), (L.T("Açık mavi"), "#DDEBF7"), (L.T("Mor"), "#7030A0"), (L.T("Gri"), "#808080"), (L.T("Açık gri"), "#EDEDED"), (L.T("Siyah"), "#000000"), (L.T("Beyaz"), "#FFFFFF") })
        {
            var swatch = new Border { Width = 14, Height = 14, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Background = hex == "" ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(hex)! };
            var mi = new MenuItem { Header = name, Icon = swatch };
            mi.Click += (_, _) => Format(fore ? L.T("Yazı rengi") : L.T("Dolgu"), s =>
            {
                if (fore) s.Font.FontColor = hex == "" ? XLColor.Black : XLColor.FromHtml(hex);
                else if (hex == "") s.Fill.PatternType = XLFillPatternValues.None;
                else s.Fill.BackgroundColor = XLColor.FromHtml(hex);
            });
            m.Items.Add(mi);
        }
        b.Click += (_, _) => { m.PlacementTarget = b; m.Placement = PlacementMode.Bottom; m.IsOpen = true; };
    }

    // ------------------------------------------------------------------ loading and cache

    void LoadSheet()
    {
        _cache.Clear(); _merges.Clear(); _colW.Clear(); _rowH.Clear();
        // Files written by other tools (openpyxl, scripts, some exporters) may store formulas without results: compute them first.
        try { _wb.RecalculateAllFormulas(); } catch { }
        var used = _ws.RangeUsed(XLCellsUsedOptions.All);
        int lastRow = used?.LastRow().RowNumber() ?? 1, lastCol = used?.LastColumn().ColumnNumber() ?? 1;
        foreach (var cell in _ws.CellsUsed(XLCellsUsedOptions.All)) _cache[CK(cell.Address.RowNumber, cell.Address.ColumnNumber)] = View(cell);
        foreach (var m in _ws.MergedRanges) _merges.Add((m.FirstRow().RowNumber(), m.FirstColumn().ColumnNumber(), m.LastRow().RowNumber(), m.LastColumn().ColumnNumber()));
        foreach (var col in _ws.ColumnsUsed(XLCellsUsedOptions.All)) { var n = col.ColumnNumber(); var wd = col.Width; if (Math.Abs(wd - _ws.ColumnWidth) > .01 || col.IsHidden) _colW[n] = col.IsHidden ? 0 : wd * 7 + 5; }
        for (int c = 1; c <= lastCol + 1; c++) { var col = _ws.Column(c); if (col.IsHidden) _colW[c] = 0; else if (Math.Abs(col.Width - _ws.ColumnWidth) > .01) _colW[c] = col.Width * 7 + 5; }
        foreach (var row in _ws.RowsUsed(XLCellsUsedOptions.All)) { if (row.IsHidden) _rowH[row.RowNumber()] = 0; else if (Math.Abs(row.Height - _ws.RowHeight) > .01) _rowH[row.RowNumber()] = row.Height * 96 / 72; }
        _view.MaxRow = Math.Max(lastRow + 200, 500); _view.MaxCol = Math.Max(lastCol + 20, 30);
        _view.OffsetX = _view.OffsetY = 0; _view.Rebuild(); _view.Select(1, 1, false);
        RefreshTabs(); ShowActive();
    }

    int LastRow() => _ws.LastRowUsed(XLCellsUsedOptions.All)?.RowNumber() ?? 1;
    int LastCol() => _ws.LastColumnUsed(XLCellsUsedOptions.All)?.ColumnNumber() ?? 1;

    static Color? ToColor(XLColor? x)
    {
        try
        {
            if (x == null || !x.HasValue) return null;
            if (x.ColorType == XLColorType.Indexed && x.Indexed == 64) return null; // otomatik
            var c = x.Color; if (c.A == 0) return null;
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch { return null; }
    }

    static CellView View(IXLCell cell)
    {
        var v = new CellView();
        try { v.Text = cell.GetFormattedString(); }
        catch { try { v.Text = cell.CachedValue.ToString(CultureInfo.CurrentCulture); } catch { v.Text = "#HATA"; } }
        try { v.IsNumber = cell.Value.IsNumber || cell.Value.IsDateTime || cell.Value.IsTimeSpan; } catch { }
        var st = cell.Style;
        v.Bold = st.Font.Bold; v.Italic = st.Font.Italic; v.Underline = st.Font.Underline != XLFontUnderlineValues.None; v.Strike = st.Font.Strikethrough;
        v.FontSize = st.Font.FontSize > 0 ? st.Font.FontSize : 11; v.FontName = string.IsNullOrWhiteSpace(st.Font.FontName) ? null : st.Font.FontName + ", Calibri, Segoe UI";
        var fore = ToColor(st.Font.FontColor); if (fore is { } f && !(f.R == 0 && f.G == 0 && f.B == 0)) v.Fore = f;
        if (st.Fill.PatternType != XLFillPatternValues.None) v.Fill = ToColor(st.Fill.BackgroundColor);
        if (v.Fill is { R: 255, G: 255, B: 255 }) v.Fill = null;
        v.Align = st.Alignment.Horizontal switch { XLAlignmentHorizontalValues.Center or XLAlignmentHorizontalValues.CenterContinuous => TextAlignment.Center, XLAlignmentHorizontalValues.Right => TextAlignment.Right, XLAlignmentHorizontalValues.Left => TextAlignment.Left, _ => null };
        v.Wrap = st.Alignment.WrapText;
        return v;
    }

    void Refresh(bool all)
    {
        if (all)
        {
            try { _wb.RecalculateAllFormulas(); } catch { }
            var keepR = _view.ActiveRow; var keepC = _view.ActiveCol; var ox = _view.OffsetX; var oy = _view.OffsetY; var ar = _view.AnchorRow; var ac = _view.AnchorCol;
            _cache.Clear(); _merges.Clear();
            foreach (var cell in _ws.CellsUsed(XLCellsUsedOptions.All)) _cache[CK(cell.Address.RowNumber, cell.Address.ColumnNumber)] = View(cell);
            foreach (var m in _ws.MergedRanges) _merges.Add((m.FirstRow().RowNumber(), m.FirstColumn().ColumnNumber(), m.LastRow().RowNumber(), m.LastColumn().ColumnNumber()));
            _view.MaxRow = Math.Max(_view.MaxRow, LastRow() + 200); _view.MaxCol = Math.Max(_view.MaxCol, LastCol() + 20);
            _view.Rebuild(); _view.OffsetX = ox; _view.OffsetY = oy; _view.AnchorRow = ar; _view.AnchorCol = ac; _view.ActiveRow = keepR; _view.ActiveCol = keepC;
        }
        _view.InvalidateVisual(); ShowActive();
    }

    void RefreshTabs()
    {
        _tabs.Children.Clear();
        foreach (var sheet in _wb.Worksheets)
        {
            var s = sheet; var on = s == _ws;
            var t = new ToggleButton { Content = s.Name, IsChecked = on, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(12, 3, 12, 3) };
            if (TryFindResource("MsSegmentToggleButtonStyle") is Style st) t.Style = st;
            t.Click += (_, _) => { CommitEditor(); _ws = s; LoadSheet(); };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = L.T("Yeniden adlandır…") }; rename.Click += (_, _) => RenameSheet(s); menu.Items.Add(rename);
            var del = new MenuItem { Header = L.T("Sayfayı sil") }; del.Click += (_, _) => DeleteSheet(s); menu.Items.Add(del);
            t.ContextMenu = menu;
            _tabs.Children.Add(t);
        }
    }

    // ------------------------------------------------------------------ input and editing

    IXLCell Active() => _ws.Cell(_view.ActiveRow, _view.ActiveCol);

    void ShowActive()
    {
        var cell = _ws.Cell(_view.ActiveRow, _view.ActiveCol);
        var sel = _view.Selection;
        _nameBox.Text = sel.r1 == sel.r2 && sel.c1 == sel.c2 ? SheetView.ColName(_view.ActiveCol) + _view.ActiveRow : $"{SheetView.ColName(sel.c1)}{sel.r1}:{SheetView.ColName(Math.Min(sel.c2, _view.MaxCol))}{Math.Min(sel.r2, _view.MaxRow)}";
        if (!_formula.IsKeyboardFocusWithin) _formula.Text = InputText(cell);
        // statistics of the selection
        double sum = 0; int count = 0, nums = 0;
        if ((long)(sel.r2 - sel.r1 + 1) * (sel.c2 - sel.c1 + 1) <= 200000)
            foreach (var kv in _cache)
            {
                int r = (int)(kv.Key >> 15), c = (int)(kv.Key & 0x7FFF);
                if (r < sel.r1 || r > sel.r2 || c < sel.c1 || c > sel.c2 || kv.Value.Text.Length == 0) continue;
                count++;
                try { var val = _ws.Cell(r, c).Value; if (val.IsNumber) { sum += val.GetNumber(); nums++; } } catch { }
            }
        _stats.Text = count > 1 ? L.F("Sayı: {0}", count) + (nums > 0 ? L.F("   Toplam: {0}   Ortalama: {1}", sum.ToString("N2", CultureInfo.CurrentCulture), (sum / nums).ToString("N2", CultureInfo.CurrentCulture)) : "") : _ws.Name;
    }

    static string InputText(IXLCell cell)
    {
        if (cell.HasFormula) return "=" + cell.FormulaA1;
        var v = cell.Value;
        if (v.IsBlank) return "";
        if (v.IsNumber) return v.GetNumber().ToString("G15", CultureInfo.CurrentCulture);
        if (v.IsDateTime) return v.GetDateTime().ToString(CultureInfo.CurrentCulture);
        if (v.IsBoolean) return v.GetBoolean() ? L.T("DOĞRU") : L.T("YANLIŞ");
        return v.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>Applies user input to a cell: '=' formula, number (local or dot decimal), percent, date, boolean, otherwise text.</summary>
    static void Apply(IXLCell cell, string input)
    {
        input = input.TrimEnd('\r', '\n');
        if (input.Length == 0) { cell.Clear(XLClearOptions.Contents); return; }
        if (input.StartsWith('=') && input.Length > 1) { cell.FormulaA1 = input[1..]; return; }
        if (input.StartsWith('\'')) { cell.Value = input[1..]; return; }
        var t = input.Trim();
        if (t.EndsWith('%') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.CurrentCulture, out var pct)) { cell.Value = pct / 100; if (cell.Style.NumberFormat.NumberFormatId == 0) cell.Style.NumberFormat.NumberFormatId = 10; return; }
        if (double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var d) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { cell.Value = d; return; }
        if (t.Equals("DOĞRU", StringComparison.CurrentCultureIgnoreCase) || t.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) { cell.Value = true; return; }
        if (t.Equals("YANLIŞ", StringComparison.CurrentCultureIgnoreCase) || t.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) { cell.Value = false; return; }
        if (t.Length >= 6 && t.Any(char.IsDigit) && (t.Contains('.') || t.Contains('/')) && DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt) && t.Count(ch => ch is '.' or '/') == 2)
        { cell.Value = dt; if (cell.Style.NumberFormat.NumberFormatId == 0) cell.Style.DateFormat.Format = "dd.MM.yyyy"; return; }
        cell.Value = input;
    }

    sealed record Snapshot(XLCellValue Value, string? Formula, string? Format);
    static Snapshot Take(IXLCell c) => new(c.HasFormula ? Blank.Value : c.Value, c.HasFormula ? c.FormulaA1 : null, null);
    static void Restore(IXLCell c, Snapshot s) { if (s.Formula != null) c.FormulaA1 = s.Formula; else { c.Clear(XLClearOptions.Contents); c.Value = s.Value; } }

    void CommitInput(int r, int c, string input) => SetCells(new[] { (r, c, input) }, L.T("Hücre"));

    void SetCells(IReadOnlyList<(int r, int c, string input)> items, string name)
    {
        // Resolved by sheet name: cell undo works even if undoing a structure change reloads the workbook.
        var sheetName = _ws.Name; IXLWorksheet Sheet() => _wb.Worksheets.TryGetWorksheet(sheetName, out var ws) ? ws : _ws;
        var before = items.Select(i => (i.r, i.c, Take(Sheet().Cell(i.r, i.c)))).ToList();
        void Do() { var sheet = Sheet(); foreach (var (r, c, input) in items) { try { Apply(sheet.Cell(r, c), input); } catch (Exception ex) { _status.Text = $"{SheetView.ColName(c)}{r}: {ex.Message}"; } } AfterEdit(sheet); }
        void Back() { var sheet = Sheet(); foreach (var (r, c, s) in before) Restore(sheet.Cell(r, c), s); AfterEdit(sheet); }
        Do(); Push(Back, Do, name);
    }

    void AfterEdit(IXLWorksheet sheet) { if (sheet != _ws) { _ws = sheet; LoadSheet(); } Refresh(true); MarkDirty(); }

    /// <summary>Undo for formatting/structure changes with an in-memory snapshot of the whole workbook.</summary>
    void Structure(string name, Action change)
    {
        var snap = SnapshotWorkbook(); var sheetName = _ws.Name;
        try { change(); } catch (Exception ex) { _status.Text = name + L.T(" yapılamadı: ") + ex.Message; return; }
        var after = SnapshotWorkbook();
        Refresh(true); RebuildSizes(); MarkDirty();
        Push(() => RestoreWorkbook(snap, sheetName), () => RestoreWorkbook(after, sheetName), name);
    }

    void Format(string name, Action<IXLStyle> change)
    {
        var sel = _view.Selection;
        var r2 = Math.Min(sel.r2, Math.Max(LastRow(), sel.r1)); var c2 = Math.Min(sel.c2, Math.Max(LastCol(), sel.c1));
        Structure(name, () => change(_ws.Range(sel.r1, sel.c1, r2, c2).Style));
    }

    MemoryStream SnapshotWorkbook() { var ms = new MemoryStream(); _wb.SaveAs(ms); ms.Position = 0; return ms; }
    void RestoreWorkbook(MemoryStream ms, string sheet)
    {
        ms.Position = 0; var copy = new MemoryStream(ms.ToArray());
        var keep = (_view.ActiveRow, _view.ActiveCol, _view.OffsetX, _view.OffsetY);
        _wb.Dispose(); _wb = new XLWorkbook(copy); _ws = _wb.Worksheets.TryGetWorksheet(sheet, out var s) ? s : _wb.Worksheets.First();
        LoadSheet(); _view.OffsetX = keep.OffsetX; _view.OffsetY = keep.OffsetY; _view.Select(keep.ActiveRow, keep.ActiveCol, false); MarkDirty();
    }

    void RebuildSizes()
    {
        _colW.Clear(); _rowH.Clear();
        for (int c = 1; c <= LastCol() + 1; c++) { var col = _ws.Column(c); if (col.IsHidden) _colW[c] = 0; else if (Math.Abs(col.Width - _ws.ColumnWidth) > .01) _colW[c] = col.Width * 7 + 5; }
        foreach (var row in _ws.RowsUsed(XLCellsUsedOptions.All)) { if (row.IsHidden) _rowH[row.RowNumber()] = 0; else if (Math.Abs(row.Height - _ws.RowHeight) > .01) _rowH[row.RowNumber()] = row.Height * 96 / 72; }
        _view.Rebuild();
    }

    sealed record Op(Action Undo, Action Redo, string Name);
    void Push(Action undo, Action redo, string name) { _undo.Push(new Op(undo, redo, name)); _redo.Clear(); }
    void Undo() { CommitEditor(); if (_undo.Count == 0) { _status.Text = L.T("Geri alınacak işlem yok."); return; } var op = _undo.Pop(); op.Undo(); _redo.Push(op); _status.Text = L.T("Geri alındı: ") + op.Name; }
    void Redo() { if (_redo.Count == 0) { _status.Text = L.T("Yinelenecek işlem yok."); return; } var op = _redo.Pop(); op.Redo(); _undo.Push(op); _status.Text = L.T("Yinelendi: ") + op.Name; }

    void MarkDirty() { _dirty = true; Title = "● " + Path.GetFileName(_savePath ?? _source) + L.T(" — Excel düzenleyici"); }

    void BeginEdit(string? initial)
    {
        CommitEditor();
        var cell = Active();
        _editor = new TextBox
        {
            Text = initial ?? InputText(cell), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41)),
            Padding = new Thickness(1, 0, 1, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13, AcceptsReturn = false,
            Background = ThemeManager.IsDark ? new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)) : Brushes.White, Foreground = ThemeManager.Brush("TextPrimaryBrush"),
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { var i = _editor.CaretIndex; _editor.Text = _editor.Text.Insert(i, "\n"); _editor.CaretIndex = i + 1; e.Handled = true; return; }
            if (e.Key is Key.Enter or Key.Tab) { var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift); CommitEditor(); if (e.Key == Key.Enter) _view.Select(_view.ActiveRow + (shift ? -1 : 1), _view.ActiveCol, false); else _view.Select(_view.ActiveRow, _view.ActiveCol + (shift ? -1 : 1), false); _view.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelEditor(); e.Handled = true; }
        };
        _editor.TextChanged += (_, _) => { if (_editor != null) _formula.Text = _editor.Text; };
        _overlay.Children.Add(_editor); PlaceEditor();
        _editor.Loaded += (_, _) => { _editor?.Focus(); if (_editor != null) { if (initial != null) _editor.CaretIndex = _editor.Text.Length; else _editor.SelectAll(); } };
    }

    void PlaceEditor()
    {
        if (_editor == null) return;
        var r = _view.CellRect(_view.ActiveRow, _view.ActiveCol);
        Canvas.SetLeft(_editor, r.X - 1); Canvas.SetTop(_editor, r.Y - 1);
        _editor.MinWidth = r.Width + 2; _editor.Height = Math.Max(r.Height + 2, 22);
    }

    void CommitEditor()
    {
        if (_editor == null) return;
        var text = _editor.Text; var r = _view.ActiveRow; var c = _view.ActiveCol;
        _overlay.Children.Remove(_editor); _editor = null;
        if (text != InputText(_ws.Cell(r, c))) CommitInput(r, c, text);
    }
    void CancelEditor() { if (_editor == null) return; _overlay.Children.Remove(_editor); _editor = null; ShowActive(); _view.Focus(); }

    void OnGridKey(object sender, KeyEventArgs e)
    {
        if (_editor != null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int r = _view.ActiveRow, c = _view.ActiveCol;
        int page = Math.Max(1, (int)(_view.ActualHeight / DefaultRowH) - 2);
        switch (e.Key)
        {
            case Key.Up: _view.Select(ctrl ? 1 : r - 1, c, shift); break;
            case Key.Down: _view.Select(ctrl ? LastRow() : r + 1, c, shift); break;
            case Key.Left: _view.Select(r, ctrl ? 1 : c - 1, shift); break;
            case Key.Right: _view.Select(r, ctrl ? LastCol() : c + 1, shift); break;
            case Key.PageDown: _view.Select(r + page, c, shift); break;
            case Key.PageUp: _view.Select(r - page, c, shift); break;
            case Key.Home: _view.Select(ctrl ? 1 : r, 1, shift); break;
            case Key.End when ctrl: _view.Select(LastRow(), LastCol(), shift); break;
            case Key.Enter: _view.Select(r + (shift ? -1 : 1), c, false); break;
            case Key.Tab: _view.Select(r, c + (shift ? -1 : 1), false); break;
            case Key.F2: BeginEdit(null); break;
            case Key.Delete: case Key.Back when !ctrl: ClearSelection(); break;
            case Key.C when ctrl: Copy(false); break;
            case Key.X when ctrl: Copy(true); break;
            case Key.V when ctrl: Paste(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.B when ctrl: Format(L.T("Kalın"), s => s.Font.Bold = !Active().Style.Font.Bold); break;
            case Key.I when ctrl: Format(L.T("İtalik"), s => s.Font.Italic = !Active().Style.Font.Italic); break;
            case Key.U when ctrl: Format(L.T("Altı çizili"), s => s.Font.Underline = Active().Style.Font.Underline == XLFontUnderlineValues.None ? XLFontUnderlineValues.Single : XLFontUnderlineValues.None); break;
            case Key.A when ctrl: _view.AnchorRow = 1; _view.AnchorCol = 1; _view.ActiveRow = Math.Max(LastRow(), 1); _view.ActiveCol = Math.Max(LastCol(), 1); _view.InvalidateVisual(); ShowActive(); break;
            default: return;
        }
        e.Handled = true;
    }

    void ClearSelection()
    {
        var s = _view.Selection; var items = new List<(int, int, string)>();
        foreach (var kv in _cache) { int rr = (int)(kv.Key >> 15), cc = (int)(kv.Key & 0x7FFF); if (rr >= s.r1 && rr <= s.r2 && cc >= s.c1 && cc <= s.c2) items.Add((rr, cc, "")); }
        if (items.Count > 0) SetCells(items, L.T("Temizle"));
    }

    void Copy(bool cut)
    {
        var s = _view.Selection; var r2 = Math.Min(s.r2, LastRow()); var c2 = Math.Min(s.c2, LastCol());
        var sb = new StringBuilder();
        for (int r = s.r1; r <= r2; r++) { for (int c = s.c1; c <= c2; c++) { if (c > s.c1) sb.Append('\t'); sb.Append(_cache.TryGetValue(CK(r, c), out var v) ? v.Text.Replace('\t', ' ').Replace("\n", " ") : ""); } sb.Append("\r\n"); }
        try { Clipboard.SetText(sb.ToString()); _status.Text = cut ? L.T("Kesildi.") : L.T("Kopyalandı."); } catch { }
        if (cut) ClearSelection();
    }

    void Paste()
    {
        string text; try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrEmpty(text)) return;
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var items = new List<(int, int, string)>();
        for (int i = 0; i < lines.Length; i++) { var cols = lines[i].Split('\t'); for (int j = 0; j < cols.Length; j++) items.Add((_view.ActiveRow + i, _view.ActiveCol + j, cols[j])); }
        SetCells(items, L.T("Yapıştır"));
        _status.Text = L.F("{0} satır yapıştırıldı.", lines.Length);
    }

    void AutoSum()
    {
        var s = _view.Selection;
        if (s.r1 != s.r2 || s.c1 != s.c2)
        {
            var targetRow = Math.Min(s.r2, LastRow()) + 1;
            var items = Enumerable.Range(s.c1, Math.Min(s.c2, LastCol()) - s.c1 + 1).Select(c => (targetRow, c, $"=SUM({SheetView.ColName(c)}{s.r1}:{SheetView.ColName(c)}{targetRow - 1})")).ToList();
            SetCells(items, L.T("Toplam")); return;
        }
        var r = s.r1 - 1; var start = r;
        while (start >= 1 && _ws.Cell(start, s.c1).Value.IsNumber) start--;
        if (start == r) { _status.Text = L.T("Üstte toplanacak sayı yok; önce sayıların olduğu aralığı seçin."); return; }
        CommitInput(s.r1, s.c1, $"=SUM({SheetView.ColName(s.c1)}{start + 1}:{SheetView.ColName(s.c1)}{r})");
    }

    void SortSelection(bool asc)
    {
        var s = _view.Selection; var r2 = Math.Min(s.r2, LastRow()); var c2 = Math.Min(s.c2, LastCol());
        if (r2 <= s.r1 + 1) { _status.Text = L.T("Sıralamak için başlık dahil en az üç satırlık bir aralık seçin."); return; }
        var col = _view.ActiveCol - s.c1 + 1;
        Structure(L.T("Sırala"), () => _ws.Range(s.r1 + 1, s.c1, r2, c2).Sort(col, asc ? XLSortOrder.Ascending : XLSortOrder.Descending));
    }

    void MergeToggle()
    {
        var s = _view.Selection;
        var existing = _ws.MergedRanges.FirstOrDefault(m => m.FirstRow().RowNumber() <= _view.ActiveRow && m.LastRow().RowNumber() >= _view.ActiveRow && m.FirstColumn().ColumnNumber() <= _view.ActiveCol && m.LastColumn().ColumnNumber() >= _view.ActiveCol);
        if (existing != null) Structure(L.T("Birleştirmeyi kaldır"), () => existing.Unmerge());
        else if (s.r1 != s.r2 || s.c1 != s.c2) Structure(L.T("Birleştir"), () => _ws.Range(s.r1, s.c1, Math.Min(s.r2, _view.MaxRow), Math.Min(s.c2, _view.MaxCol)).Merge());
    }

    void GoTo(string address)
    {
        try { var a = _ws.Cell(address.Trim().ToUpperInvariant()).Address; _view.Select(a.RowNumber, a.ColumnNumber, false); }
        catch { _status.Text = L.T("Geçersiz hücre adresi."); }
    }

    void AddSheet()
    {
        var name = L.T("Sayfa") + (_wb.Worksheets.Count + 1); while (_wb.Worksheets.Contains(name)) name += "_";
        Structure(L.T("Sayfa ekle"), () => _ws = _wb.Worksheets.Add(name)); LoadSheet();
    }

    void RenameSheet(IXLWorksheet s)
    {
        var w = new Window { Title = L.T("Sayfa adı"), Width = 320, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(14) }; var tb = new TextBox { Text = s.Name }; sp.Children.Add(tb);
        var ok = new Button { Content = L.T("Tamam"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), MinWidth = 80 }; ok.Click += (_, _) => w.DialogResult = true; sp.Children.Add(ok);
        w.Content = sp; w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        if (w.ShowDialog() == true && tb.Text.Trim().Length > 0) { Structure(L.T("Yeniden adlandır"), () => s.Name = tb.Text.Trim()); RefreshTabs(); }
    }

    void DeleteSheet(IXLWorksheet s)
    {
        if (_wb.Worksheets.Count == 1) { _status.Text = L.T("Son sayfa silinemez."); return; }
        if (MessageBox.Show(this, L.F("\"{0}\" sayfası silinsin mi?", s.Name), L.T("Excel düzenleyici"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Structure(L.T("Sayfa sil"), () => { s.Delete(); _ws = _wb.Worksheets.First(); }); LoadSheet();
    }

    void SyncBars()
    {
        var vh = Math.Max(0, _view.ActualHeight - SheetView.HeaderH); var vw = Math.Max(0, _view.ActualWidth - SheetView.HeaderW);
        _vbar.Maximum = Math.Max(0, _view.TotalHeight - vh); _vbar.ViewportSize = vh; _vbar.LargeChange = vh; _vbar.SmallChange = DefaultRowH; _vbar.Value = Math.Min(_view.OffsetY, _vbar.Maximum);
        _hbar.Maximum = Math.Max(0, _view.TotalWidth - vw); _hbar.ViewportSize = vw; _hbar.LargeChange = vw; _hbar.SmallChange = DefaultColW; _hbar.Value = Math.Min(_view.OffsetX, _hbar.Maximum);
        // the row area grows while scrolling down (endless table feeling)
        if (_view.OffsetY + vh > _view.TotalHeight - 200 && _view.MaxRow < 1_048_576) { _view.MaxRow = Math.Min(1_048_576, _view.MaxRow + 500); _view.Rebuild(); }
    }

    // ------------------------------------------------------------------ saving

    async Task<bool> SaveAsync(bool forceDialog)
    {
        CommitEditor();
        var path = _savePath;
        if (path == null || forceDialog)
        {
            var ext = Path.GetExtension(_source).ToLowerInvariant() is ".xlsm" ? ".xlsm" : ".xlsx";
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = L.T("Excel dosyasını kaydet"), Filter = ext == ".xlsm" ? L.T("Makro içeren çalışma kitabı (*.xlsm)|*.xlsm") : L.T("Excel çalışma kitabı (*.xlsx)|*.xlsx"), FileName = Path.GetFileNameWithoutExtension(_source) + ext, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AddExtension = true };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            _status.Text = L.T("Kaydediliyor…");
            var wb = _wb; var dest = path;
            await Task.Run(() => { var tmp = dest + ".kayit.tmp"; wb.SaveAs(tmp); File.Move(tmp, dest, true); });
            _savePath = path; _dirty = false; Title = Path.GetFileName(path) + L.T(" — Excel düzenleyici");
            _status.Text = L.T("Kaydedildi: ") + path;
            return true;
        }
        catch (Exception ex) { _status.Text = L.T("Kaydedilemedi: ") + ex.Message; MessageBox.Show(this, L.T("Kaydedilemedi:\n") + ex.Message, L.T("Excel düzenleyici"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    bool _closeAfterSave;
    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitEditor();
        if (!_dirty || _closeAfterSave) { _wb.Dispose(); return; }
        var a = MessageBox.Show(this, L.T("Kaydedilmemiş değişiklikler var. Kaydedilsin mi?"), L.T("Excel düzenleyici"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (a == MessageBoxResult.No) { _wb.Dispose(); return; }
        e.Cancel = true;
        if (a == MessageBoxResult.Yes && await SaveAsync(false)) { _closeAfterSave = true; Close(); }
    }

    static void Shell(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }
}

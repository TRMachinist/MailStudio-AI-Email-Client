using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MailStudio.Services;
using MailStudio.Services.Costing;
using MailStudio.Services.Inspection;
using MailStudio.Views.Calendar;
using Microsoft.Win32;

namespace MailStudio.Views.Inspection;

/// <summary>
/// Quick ballooning for first article inspection: characteristics are found on the drawing (text layer or OCR),
/// numbered in reading order and listed for checking. The inspector can remove, edit or add (drag a box on the page)
/// characteristics, then save a ballooned copy of the PDF and the AS9102 Form 1 / Form 3 workbook.
/// </summary>
public sealed class BalloonWindow : Window
{
    readonly string _pdf;
    readonly Func<string, object, CancellationToken, Task<JsonElement>> _call;
    readonly CancellationTokenSource _life = new();
    readonly ObservableCollection<Characteristic> _items = new();
    readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended };
    readonly Image _image = new() { Stretch = Stretch.None };
    readonly Canvas _overlay = new() { Background = Brushes.Transparent };
    readonly Grid _surface = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly ScaleTransform _zoom = new(1, 1);
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White };
    readonly TextBlock _status = PimUi.Text("", 12, brush: "TextSecondaryBrush");
    readonly TextBlock _busy = PimUi.Text("", 13, brush: "TextMutedBrush");
    readonly TextBox _partNo = Field(), _rev = Field(60), _partName = Field(200), _org = Field(200), _customer = Field(160), _report = Field(110);
    readonly ToggleButton _addMode = new() { Content = L.T("Elle ekle"), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0) };
    readonly TextBlock _pageText = PimUi.Text("", 12);
    double _scale = 1, _pageW, _pageH;
    int _page, _pages = 1;
    Point? _dragStart; Rectangle? _dragBox;
    readonly Dictionary<int, (double W, double H)> _pageSizes = new();

    /// <summary>Completes after the first extraction (for the self-test).</summary>
    public Task Ready => _ready.Task;
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<Characteristic> Items => _items;

    static TextBox Field(double w = 140) => new() { Height = 26, Width = w, VerticalContentAlignment = VerticalAlignment.Center };

    public BalloonWindow(string pdfPath, Func<string, object, CancellationToken, Task<JsonElement>> call)
    {
        _pdf = pdfPath; _call = call;
        UiTranslator.SetSkip(this, true);
        Title = L.T("Balonlama ve AS9102 FAI") + " – " + System.IO.Path.GetFileName(pdfPath);
        Width = 1320; Height = 860; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var (part, rev) = RevisionService.ParsePartAndRevision("", System.IO.Path.GetFileName(pdfPath));
        _partNo.Text = part; _rev.Text = rev; _partName.Text = "";
        try { _org.Text = CostEstimator.LoadSettings().Company.Name; } catch { }
        _report.Text = $"FAI-{DateTime.Today:yyyyMMdd}";

        // header (Form 1 fields)
        var head = new WrapPanel { Margin = new Thickness(12, 10, 12, 4) };
        void Labeled(string label, Control c)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 4) };
            sp.Children.Add(PimUi.Text(label, 11.5, brush: "TextSecondaryBrush")); sp.Children.Add(c); head.Children.Add(sp);
        }
        Labeled(L.T("Parça numarası"), _partNo); Labeled(L.T("Rev"), _rev); Labeled(L.T("Parça adı"), _partName);
        Labeled(L.T("Üretici"), _org); Labeled(L.T("Müşteri"), _customer); Labeled(L.T("FAI rapor no"), _report);

        // toolbar
        var bar = new WrapPanel { Margin = new Thickness(12, 2, 12, 6) };
        Button Btn(string text, RoutedEventHandler click, string style = "RibbonCmd", string? tip = null)
        {
            var b = new Button { Content = text, Height = 28, MinWidth = 34, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            b.SetResourceReference(StyleProperty, style); b.Click += click; bar.Children.Add(b); return b;
        }
        Btn("◀", async (_, _) => await ShowPageAsync(_page - 1), tip: L.T("Önceki sayfa"));
        _pageText.VerticalAlignment = VerticalAlignment.Center; _pageText.Margin = new Thickness(0, 0, 6, 0); bar.Children.Add(_pageText);
        Btn("▶", async (_, _) => await ShowPageAsync(_page + 1), tip: L.T("Sonraki sayfa"));
        Btn(L.T("Sığdır"), (_, _) => Fit());
        Btn("+", (_, _) => SetZoom(_zoom.ScaleX * 1.25)); Btn("−", (_, _) => SetZoom(_zoom.ScaleX / 1.25));
        _addMode.SetResourceReference(StyleProperty, "MsSegmentToggleButtonStyle");
        _addMode.ToolTip = L.T("Sayfada bir alanı sürükleyerek karakteristik ekleyin");
        _addMode.Checked += (_, _) => { DrawOverlay(); _status.Text = L.T("Eklemek istediğiniz ölçünün çevresine sayfada bir kutu sürükleyin (Esc: bitir)."); };
        _addMode.Unchecked += (_, _) => DrawOverlay();
        bar.Children.Add(_addMode);
        Btn(L.T("Seçilenleri sil"), (_, _) => DeleteSelected(), tip: "Delete");
        Btn(L.T("Yeniden numarala"), (_, _) => { Characteristic.Renumber(_items); Resort(); });
        Btn(L.T("Balonlu PDF kaydet…"), async (_, _) => await SavePdfAsync(null), "RibbonPrimaryCmd");
        Btn(L.T("AS9102 Excel…"), (_, _) => SaveExcel(null), "RibbonPrimaryCmd");

        // page
        _surface.Children.Add(_image); _surface.Children.Add(_overlay);
        _surface.LayoutTransform = _zoom; _scroll.Content = _surface;
        _scroll.PreviewMouseWheel += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.Control) { SetZoom(_zoom.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15)); e.Handled = true; } };
        _overlay.MouseLeftButtonDown += OnDown; _overlay.MouseMove += OnMove; _overlay.MouseLeftButtonUp += OnUp;
        var view = new Grid(); view.Children.Add(_scroll);
        _busy.HorizontalAlignment = HorizontalAlignment.Center; _busy.VerticalAlignment = VerticalAlignment.Center; _busy.TextWrapping = TextWrapping.Wrap; _busy.Margin = new Thickness(30);
        view.Children.Add(_busy);

        // list
        TextColumn("#", nameof(Characteristic.Id), 40, true);
        TextColumn(L.T("Bölge"), nameof(Characteristic.Zone), 52);
        TextColumn(L.T("Tür"), nameof(Characteristic.TypeText), 90, true);
        TextColumn(L.T("Gereklilik"), nameof(Characteristic.Requirement), 190);
        TextColumn(L.T("Nominal"), nameof(Characteristic.Nominal), 64);
        TextColumn("+", nameof(Characteristic.Upper), 50);
        TextColumn("−", nameof(Characteristic.Lower), 50);
        TextColumn(L.T("Adet"), nameof(Characteristic.Quantity), 44);
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = L.T("Kontrol"), Binding = new Binding(nameof(Characteristic.Review)), Width = 56 });
        _grid.ItemsSource = _items;
        _grid.SelectionChanged += (_, _) => DrawOverlay();
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(DrawOverlay), System.Windows.Threading.DispatcherPriority.Background);
        _grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && e.OriginalSource is not TextBox) { DeleteSelected(); e.Handled = true; } };
        _grid.MouseDoubleClick += async (_, _) => { if (_grid.SelectedItem is Characteristic c) await FocusAsync(c); };
        var listHost = new DockPanel { Margin = new Thickness(8, 0, 12, 0) };
        var hint = PimUi.Text(L.T("Sarı satırlar OCR ile okundu: değeri resimde doğrulayıp \"Kontrol\" işaretini kaldırın. Çift tık: balona git."), 11.5, brush: "TextMutedBrush");
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(hint, Dock.Top); listHost.Children.Add(hint); listHost.Children.Add(_grid);
        _grid.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is Characteristic { Review: true }) e.Row.Background = new SolidColorBrush(Color.FromArgb(70, 255, 200, 0));
            else e.Row.ClearValue(BackgroundProperty);
        };

        var body = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
        var frame = new Border { Child = view, Margin = new Thickness(12, 0, 0, 0), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        body.Children.Add(frame);
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(split, 1); body.Children.Add(split);
        Grid.SetColumn(listHost, 2); body.Children.Add(listHost);

        var foot = new DockPanel { Margin = new Thickness(12, 8, 12, 10) };
        var close = new Button { Content = L.T("Kapat"), MinWidth = 90, Height = 30, IsCancel = true };
        close.SetResourceReference(StyleProperty, "RibbonCmd"); close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); foot.Children.Add(close);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextWrapping = TextWrapping.Wrap; foot.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        root.Children.Add(body);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _addMode.IsChecked == true) { _addMode.IsChecked = false; e.Handled = true; } };
        Closed += (_, _) => _life.Cancel();
        Loaded += async (_, _) => await ExtractAsync();
    }

    void TextColumn(string header, string path, double width, bool readOnly = false) =>
        _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path) { TargetNullValue = "" }, Width = width, IsReadOnly = readOnly });

    async Task ExtractAsync()
    {
        _busy.Text = L.T("Resimdeki ölçüler okunuyor… (taranmış resimlerde OCR birkaç saniye sürebilir)");
        try
        {
            var r = await _call("balloon_extract", new { path = _pdf }, _life.Token);
            foreach (var p in r.GetProperty("pages").EnumerateArray())
                _pageSizes[p.GetProperty("page").GetInt32()] = (p.GetProperty("width").GetDouble(), p.GetProperty("height").GetDouble());
            foreach (var c in Characteristic.FromExtract(r)) _items.Add(c);
            // the title block wins over the file name ("flange-DN100-revC.pdf" → DWG NO. NW-1042)
            if (r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                if (t.TryGetProperty("part_number", out var pn) && pn.GetString() is { Length: > 0 } pns) _partNo.Text = pns;
                if (t.TryGetProperty("revision", out var rv) && rv.GetString() is { Length: > 0 } rvs) _rev.Text = rvs;
            }
            var gen = r.TryGetProperty("general_class", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
            var notice = r.TryGetProperty("notice", out var n) ? n.GetString() : "";
            _status.Text = L.F("{0} karakteristik bulundu.", _items.Count)
                           + (gen != null ? " " + L.F("Genel tolerans ISO 2768-{0} toleranssız ölçülere uygulandı.", gen) : " " + L.T("Genel tolerans notu bulunamadı; toleranssız ölçülerin sınırlarını girin."))
                           + (string.IsNullOrEmpty(notice) ? "" : " " + L.T("OCR ile okunan değerler tahmindir; her balonu resimde doğrulayın."));
            await ShowPageAsync(0);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _busy.Text = L.T("Resim okunamadı: ") + ex.Message; }
        finally { _ready.TrySetResult(); }
    }

    async Task ShowPageAsync(int page)
    {
        if (page < 0 || (page >= _pages && _image.Source != null)) return;
        try
        {
            var r = await _call("balloon_render", new { path = _pdf, page }, _life.Token);
            var png = Convert.FromBase64String(r.GetProperty("image").GetString()!);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(png)) { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
            bmp.Freeze();
            _page = page; _pages = r.GetProperty("pages").GetInt32(); _scale = r.GetProperty("scale").GetDouble();
            _pageW = r.GetProperty("width").GetDouble(); _pageH = r.GetProperty("height").GetDouble();
            _pageSizes[page] = (_pageW, _pageH);
            _image.Source = bmp; _image.Width = bmp.PixelWidth; _image.Height = bmp.PixelHeight;
            _overlay.Width = bmp.PixelWidth; _overlay.Height = bmp.PixelHeight;
            _pageText.Text = L.F("Sayfa {0} / {1}", _page + 1, _pages);
            _busy.Visibility = Visibility.Collapsed;
            DrawOverlay(); Fit();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _busy.Text = L.T("Sayfa gösterilemedi: ") + ex.Message; _busy.Visibility = Visibility.Visible; }
    }

    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(214, 40, 40));
    static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(214, 140, 0));

    void DrawOverlay()
    {
        _overlay.Children.Clear();
        var selected = _grid.SelectedItems.OfType<Characteristic>().ToHashSet();
        foreach (var c in _items.Where(i => i.Page == _page))
        {
            var brush = c.Review ? Amber : Red;
            var sel = selected.Contains(c);
            var box = new Rectangle { Width = c.W * _scale + 6, Height = c.H * _scale + 6, Stroke = brush, StrokeThickness = sel ? 3 : 1.2, StrokeDashArray = sel ? null : new DoubleCollection { 3, 2 }, Fill = sel ? new SolidColorBrush(Color.FromArgb(40, 214, 40, 40)) : null };
            Canvas.SetLeft(box, c.X * _scale - 3); Canvas.SetTop(box, c.Y * _scale - 3); _overlay.Children.Add(box);
            var (bx, by, br) = c.Balloon is { Length: 3 } b ? (b[0], b[1], b[2]) : (c.X - 8, c.Y - 8, 6.0);
            double cx = bx * _scale, cy = by * _scale, r = br * _scale;
            // leader to the nearest point of the feature box
            double fx = Math.Clamp(cx, c.X * _scale, (c.X + c.W) * _scale), fy = Math.Clamp(cy, c.Y * _scale, (c.Y + c.H) * _scale);
            var d = Math.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
            if (d > r) _overlay.Children.Add(new Line { X1 = cx + (fx - cx) / d * r, Y1 = cy + (fy - cy) / d * r, X2 = fx, Y2 = fy, Stroke = brush, StrokeThickness = Math.Max(1, r / 9) });
            var circle = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = brush, StrokeThickness = sel ? Math.Max(2, r / 4) : Math.Max(1.2, r / 7), Fill = Brushes.White, Tag = c, Cursor = Cursors.Hand };
            circle.MouseLeftButtonDown += (_, e) => { if (_addMode.IsChecked == true) return; _grid.SelectedItem = c; _grid.ScrollIntoView(c); e.Handled = true; };
            Canvas.SetLeft(circle, cx - r); Canvas.SetTop(circle, cy - r); _overlay.Children.Add(circle);
            var label = new TextBlock { Text = c.Id.ToString(), FontSize = r * (c.Id < 10 ? 1.15 : c.Id < 100 ? 0.95 : 0.75), FontWeight = FontWeights.SemiBold, Foreground = brush, IsHitTestVisible = false };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, cx - label.DesiredSize.Width / 2); Canvas.SetTop(label, cy - label.DesiredSize.Height / 2); _overlay.Children.Add(label);
        }
        _overlay.Cursor = _addMode.IsChecked == true ? Cursors.Cross : null;
    }

    // ------------------------------------------------------------------ adding a characteristic by dragging a box
    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_addMode.IsChecked != true) return;
        _dragStart = e.GetPosition(_overlay);
        _dragBox = new Rectangle { Stroke = Red, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 2 } };
        Canvas.SetLeft(_dragBox, _dragStart.Value.X); Canvas.SetTop(_dragBox, _dragStart.Value.Y);
        _overlay.Children.Add(_dragBox); _overlay.CaptureMouse(); e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } s || _dragBox == null) return;
        var p = e.GetPosition(_overlay);
        Canvas.SetLeft(_dragBox, Math.Min(s.X, p.X)); Canvas.SetTop(_dragBox, Math.Min(s.Y, p.Y));
        _dragBox.Width = Math.Abs(p.X - s.X); _dragBox.Height = Math.Abs(p.Y - s.Y);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } s) return;
        var p = e.GetPosition(_overlay); _overlay.ReleaseMouseCapture(); _dragStart = null;
        if (Math.Abs(p.X - s.X) < 4 || Math.Abs(p.Y - s.Y) < 4) { DrawOverlay(); return; }
        AddManual(new Rect(Math.Min(s.X, p.X) / _scale, Math.Min(s.Y, p.Y) / _scale, Math.Abs(p.X - s.X) / _scale, Math.Abs(p.Y - s.Y) / _scale));
    }

    /// <summary>Adds a characteristic for a box in page points (top-left origin) on the current page.</summary>
    public Characteristic AddManual(Rect r)
    {
        var radius = Math.Max(5.0, Math.Min(_pageW, _pageH) / 70);
        var c = new Characteristic
        {
            Page = _page, X = r.X, Y = r.Y, W = r.Width, H = r.Height, Type = "manual", Source = "manual",
            Zone = Zone(r.X + r.Width / 2, r.Y + r.Height / 2), Balloon = new[] { Math.Max(radius + 2, r.X - radius * 1.6), Math.Max(radius + 2, r.Y - radius * 1.6), radius }
        };
        _items.Add(c);
        Characteristic.Renumber(_items); Resort();
        _grid.SelectedItem = c; _grid.ScrollIntoView(c);
        _grid.CurrentCell = new DataGridCellInfo(c, _grid.Columns[3]);
        _grid.Focus(); _grid.BeginEdit();
        return c;
    }

    string Zone(double x, double y)
    {
        int cols = 8, rows = 6;
        if (_pageW < _pageH) (cols, rows) = (rows, cols);
        var col = Math.Min(cols, (int)(x / _pageW * cols) + 1); var row = Math.Min(rows - 1, (int)(y / _pageH * rows));
        return $"{"ABCDEFGHJK"[row]}{col}";
    }

    void DeleteSelected()
    {
        var sel = _grid.SelectedItems.OfType<Characteristic>().ToList();
        if (sel.Count == 0) return;
        foreach (var c in sel) _items.Remove(c);
        Characteristic.Renumber(_items); Resort();
        _status.Text = L.F("{0} karakteristik silindi; numaralar yeniden verildi.", sel.Count);
    }

    void Resort()
    {
        var ordered = _items.OrderBy(i => i.Id).ToList();
        _items.Clear(); foreach (var c in ordered) _items.Add(c);
        _grid.Items.Refresh(); DrawOverlay();
    }

    async Task FocusAsync(Characteristic c)
    {
        if (c.Page != _page) await ShowPageAsync(c.Page);
        var z = Math.Clamp(_zoom.ScaleX < 0.9 ? 1.2 : _zoom.ScaleX, 0.2, 4); SetZoom(z); UpdateLayout();
        _scroll.ScrollToHorizontalOffset(Math.Max(0, (c.X + c.W / 2) * _scale * z - _scroll.ViewportWidth / 2));
        _scroll.ScrollToVerticalOffset(Math.Max(0, (c.Y + c.H / 2) * _scale * z - _scroll.ViewportHeight / 2));
    }

    void SetZoom(double z) => _zoom.ScaleX = _zoom.ScaleY = Math.Clamp(z, 0.05, 6);

    void Fit()
    {
        if (_image.Source is not BitmapSource b) return;
        UpdateLayout();
        var w = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth - 4 : 700; var h = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight - 4 : 600;
        SetZoom(Math.Min(w / b.PixelWidth, h / b.PixelHeight));
    }

    FaiHeader Header() => new()
    {
        PartNumber = _partNo.Text.Trim(), Revision = _rev.Text.Trim(), PartName = _partName.Text.Trim(), DrawingNumber = _partNo.Text.Trim(),
        Organization = _org.Text.Trim(), Customer = _customer.Text.Trim(), ReportNumber = _report.Text.Trim(), DrawingFile = System.IO.Path.GetFileName(_pdf)
    };

    string BaseName() => System.IO.Path.GetFileNameWithoutExtension(_pdf);

    /// <summary>Writes the ballooned PDF copy (dest null = ask). Returns the path or null.</summary>
    public async Task<string?> SavePdfAsync(string? dest)
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_items.Count == 0) { _status.Text = L.T("Balonlanacak karakteristik yok."); return null; }
        if (dest == null)
        {
            var d = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = BaseName() + " " + L.T("balonlu") + ".pdf" };
            if (d.ShowDialog(this) != true) return null;
            dest = d.FileName;
        }
        try
        {
            _status.Text = L.T("Balonlu PDF yazılıyor…");
            await _call("balloon_save", new { path = _pdf, dest, items = _items.Select(i => i.ToWorker()).ToList() }, _life.Token);
            _status.Text = L.F("Balonlu PDF kaydedildi: {0}", dest);
            return dest;
        }
        catch (Exception ex) { _status.Text = L.T("Balonlu PDF yazılamadı: ") + ex.Message; return null; }
    }

    /// <summary>Writes the AS9102 workbook (dest null = ask). Returns the path or null.</summary>
    public string? SaveExcel(string? dest)
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (dest == null)
        {
            var d = new SaveFileDialog { Filter = "Excel|*.xlsx", FileName = BaseName() + " AS9102 FAI.xlsx" };
            if (d.ShowDialog(this) != true) return null;
            dest = d.FileName;
        }
        try
        {
            FaiReport.ExportAs9102(dest, Header(), _items);
            _status.Text = L.F("AS9102 çalışma kitabı kaydedildi: {0}", dest);
            return dest;
        }
        catch (Exception ex) { _status.Text = L.T("Excel yazılamadı: ") + ex.Message; return null; }
    }
}

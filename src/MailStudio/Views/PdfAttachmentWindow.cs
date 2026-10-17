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
using System.Windows.Threading;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// Lightweight PDF studio: continuously scrolling pages, mouse wheel / Ctrl+wheel zoom, panning with the hand tool,
/// page rotation, text selection from the text layer or OCR, object select/move/delete, highlight, pen, shapes, notes,
/// text insertion and text correction. Edits are written to a new PDF copy without touching the source file.
/// Coordinates are PDF points, unrotated page, origin top-left (same convention as the worker).
/// </summary>
public sealed partial class PdfAttachmentWindow : Window
{
    const double PtToDip = 96.0 / 72.0, PageGap = 14;
    static readonly double[] ZoomSteps = { .25, .33, .5, .67, .75, .9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8 };

    sealed class PageState
    {
        public int Index; public double W, H; public int BaseRotation, UserRotation;
        public Border Frame = null!; public Grid Sheet = null!; public Image Image = null!; public Canvas Overlay = null!, Marks = null!, Selection = null!, Temp = null!;
        public double RenderedScale; public bool Queued; public List<PdfWord>? Words; public bool WordsFromOcr, OcrTried; public List<PdfObject>? Objects;
        public Image? Thumb; public bool ThumbDone; public double Top;
        public int Rotation => ((BaseRotation + UserRotation) % 360 + 360) % 360;
        public double ViewW => Rotation % 180 == 0 ? W : H;
        public double ViewH => Rotation % 180 == 0 ? H : W;
    }
    public sealed record PdfWord(double X, double Y, double W, double H, string Text, bool Nl, bool Ocr) { public Rect Box => new(X, Y, W, H); }
    public sealed record PdfObject(int Index, string Type, double X, double Y, double W, double H) { public Rect Box => new(X, Y, W, H); }

    readonly string _path;
    readonly TechnicalWorker _worker; readonly CancellationTokenSource _life = new();
    readonly ComposeAiOptions _options;
    readonly List<PageState> _pageStates = new();
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true, PanningMode = PanningMode.Both };
    readonly StackPanel _stack = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 16, 24, 24) };
    readonly ListBox _thumbs = new() { BorderThickness = new Thickness(0) };
    readonly TextBox _pageBox = new() { Width = 44, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _pageCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
    readonly ComboBox _zoomBox = new() { Width = 92, IsEditable = true };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _selInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
    readonly TextBlock _zoomInfo = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly ColumnDefinition _thumbCol = new() { Width = new GridLength(150) }, _sideCol = new() { Width = new GridLength(AppSettings.Shared.PdfSidePanel ? 340 : 0) };
    readonly DispatcherTimer _renderDebounce = new() { Interval = TimeSpan.FromMilliseconds(140) };
    double _zoom = 1; int _page, _pages; bool _loaded, _renderLoopRunning, _fitWidthMode, _fitPageMode;
    readonly Queue<int> _renderQueue = new(); readonly Queue<int> _thumbQueue = new();
    Point? _panStart; Point _panOffset;
    string _method = L.T("PDF metin katmanı");

    readonly string? _sourceDocument; readonly string _titleBase;

    /// <param name="sourceDocument">If the PDF was produced from an Office document, the original document (for the title, "open in default program" and the save name).</param>
    public PdfAttachmentWindow(string path, ComposeAiOptions options, string? workerExecutable = null, string? sourceDocument = null, string? sourceKind = null)
    {
        _path = path; _options = options; _worker = new(workerExecutable); _sourceDocument = sourceDocument;
        _titleBase = sourceDocument != null ? $"{Path.GetFileName(sourceDocument)} — {sourceKind ?? "Belge"}" : Path.GetFileName(path) + " — PDF";
        Title = _titleBase; Width = 1320; Height = 880; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;

        root.Children.Add(BuildToolbar());

        var body = new Grid();
        body.ColumnDefinitions.Add(_thumbCol); body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(_sideCol);
        Grid.SetRow(body, 1); root.Children.Add(body);
        var thumbHost = new Border { BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), Child = _thumbs };
        _thumbs.Background = Brushes.Transparent;
        ScrollViewer.SetHorizontalScrollBarVisibility(_thumbs, ScrollBarVisibility.Disabled);
        body.Children.Add(thumbHost);
        _thumbs.SelectionChanged += (_, _) => { if (_thumbs.SelectedIndex >= 0 && _thumbs.SelectedIndex != _page && _thumbs.IsKeyboardFocusWithin | _thumbs.IsMouseOver) GoToPage(_thumbs.SelectedIndex); };
        var thumbMenu = new ContextMenu();
        thumbMenu.Items.Add(Menu(L.T("Bu sayfayı sağa döndür"), "", () => RotatePages(new[] { _page }, 90)));
        thumbMenu.Items.Add(Menu(L.T("Tüm sayfaları sağa döndür"), "", () => RotatePages(Enumerable.Range(0, _pages), 90)));
        thumbMenu.Items.Add(Menu(L.T("Tüm sayfaları sola döndür"), "", () => RotatePages(Enumerable.Range(0, _pages), -90)));
        _thumbs.ContextMenu = thumbMenu;

        _scroll.Content = _stack; _scroll.Background = ThemeManager.Brush("BgAppCanvasBrush");
        Grid.SetColumn(_scroll, 1); body.Children.Add(_scroll);
        _scroll.ScrollChanged += (_, _) => { UpdateCurrentPage(); ScheduleRender(); };
        _scroll.PreviewMouseWheel += OnWheel;
        _scroll.PreviewMouseDown += OnPanDown; _scroll.PreviewMouseMove += OnPanMove; _scroll.PreviewMouseUp += OnPanUp;
        _scroll.SizeChanged += (_, _) => { if (_fitWidthMode && _loaded) FitWidth(); else if (_fitPageMode && _loaded) FitPage(); else ScheduleRender(); };

        var side = BuildSidePanel(); Grid.SetColumn(side, 2); body.Children.Add(side);

        var statusBar = new Border { Padding = new Thickness(12, 5, 12, 5), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var sd = new DockPanel(); statusBar.Child = sd;
        DockPanel.SetDock(_zoomInfo, Dock.Right); DockPanel.SetDock(_selInfo, Dock.Right);
        _selInfo.Foreground = _zoomInfo.Foreground = ThemeManager.Brush("TextSecondaryBrush");
        sd.Children.Add(_zoomInfo); sd.Children.Add(_selInfo); sd.Children.Add(_status);
        Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);

        _renderDebounce.Tick += (_, _) => { _renderDebounce.Stop(); QueueVisible(); };
        PreviewKeyDown += OnKey;
        Loaded += async (_, _) => await OpenAsync();
        Closing += OnClosingAsk;
        Closed += (_, _) => { _life.Cancel(); _renderDebounce.Stop(); _worker.Dispose(); };
        SetTool(Tool.Text);
    }

    static MenuItem Menu(string header, string icon, Action run)
    { var m = new MenuItem { Header = header, Icon = icon }; m.Click += (_, _) => run(); return m; }

    Button IconBtn(string glyph, string tip, Action run)
    {
        var b = new Button { Content = glyph, ToolTip = tip };
        if (TryFindResource("IconButton") is Style s) b.Style = s; else b.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        b.FontSize = 14; b.Width = 30; b.Height = 30; b.Click += (_, _) => run(); return b;
    }

    static Border Sep() => new() { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), Background = ThemeManager.Brush("BorderSubtleBrush"), VerticalAlignment = VerticalAlignment.Center };

    UIElement BuildToolbar()
    {
        var bar = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var wrap = new WrapPanel(); bar.Child = wrap;
        wrap.Children.Add(IconBtn("", L.T("Sayfa küçük resimlerini göster/gizle"), () => _thumbCol.Width = _thumbCol.Width.Value > 0 ? new GridLength(0) : new GridLength(150)));
        wrap.Children.Add(IconBtn("", L.T("Farklı kaydet… (Ctrl+S) — düzenlemeler yeni PDF'ye yazılır, ek dosyası değişmez"), async () => await SaveAsAsync()));
        wrap.Children.Add(IconBtn("", _sourceDocument != null ? L.T("Belgeyi kendi programında (Word/Excel/PowerPoint) aç") : L.T("Varsayılan PDF programında aç"), () => { try { Process.Start(new ProcessStartInfo(_sourceDocument ?? _path) { UseShellExecute = true }); } catch (Exception ex) { _status.Text = ex.Message; } }));
        if (_sourceDocument != null)
        {
            var edit = new Button { ToolTip = L.T("Belgeyi MailStudio düzenleyicisinde aç (değişiklikler yeni dosyaya kaydedilir)"), Padding = new Thickness(10, 3, 12, 3), Height = 30, Margin = new Thickness(4, 0, 0, 0) };
            var esp = new StackPanel { Orientation = Orientation.Horizontal };
            esp.Children.Add(new TextBlock { Text = "\uE70F", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            esp.Children.Add(new TextBlock { Text = L.T("Düzenle"), VerticalAlignment = VerticalAlignment.Center });
            edit.Content = esp;
            if (TryFindResource("MsPrimaryButtonStyle") is Style ps) edit.Style = ps;
            edit.Click += async (_, _) =>
            {
                edit.IsEnabled = false;
                try { await FileViewers.OpenEditorAsync(_sourceDocument, this, t => _status.Text = t); }
                catch (Exception ex) { _status.Text = L.T("Düzenleyici açılamadı: ") + ex.Message; }
                finally { edit.IsEnabled = true; }
            };
            wrap.Children.Add(edit);
        }
        wrap.Children.Add(Sep());
        wrap.Children.Add(IconBtn("", L.T("Önceki sayfa"), () => GoToPage(_page - 1)));
        _pageBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && int.TryParse(_pageBox.Text, out var n)) { GoToPage(n - 1); _scroll.Focus(); } };
        wrap.Children.Add(_pageBox); wrap.Children.Add(_pageCount);
        wrap.Children.Add(IconBtn("", L.T("Sonraki sayfa"), () => GoToPage(_page + 1)));
        wrap.Children.Add(Sep());
        wrap.Children.Add(IconBtn("", L.T("Uzaklaştır (Ctrl −)"), () => StepZoom(-1, null)));
        foreach (var z in new[] { "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%", L.T("Genişliğe sığdır"), L.T("Sayfaya sığdır") }) _zoomBox.Items.Add(z);
        _zoomBox.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => { if (_zoomBox.SelectedItem is string s) ApplyZoomText(s); }));
        _zoomBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyZoomText(_zoomBox.Text); _scroll.Focus(); } };
        wrap.Children.Add(_zoomBox);
        wrap.Children.Add(IconBtn("", L.T("Yakınlaştır (Ctrl +)"), () => StepZoom(1, null)));
        wrap.Children.Add(IconBtn("", L.T("Genişliğe sığdır"), FitWidth));
        wrap.Children.Add(IconBtn("", L.T("Sayfaya sığdır"), FitPage));
        wrap.Children.Add(Sep());
        wrap.Children.Add(IconBtn("", L.T("Sayfayı sola döndür (Ctrl+Shift+−)"), () => RotatePages(new[] { _page }, -90)));
        wrap.Children.Add(IconBtn("", L.T("Sayfayı sağa döndür (Ctrl+Shift++)"), () => RotatePages(new[] { _page }, 90)));
        wrap.Children.Add(Sep());
        wrap.Children.Add(BuildToolButtons());
        wrap.Children.Add(Sep());
        wrap.Children.Add(BuildColorPicker());
        wrap.Children.Add(Sep());
        wrap.Children.Add(IconBtn("", L.T("Geri al (Ctrl+Z)"), Undo));
        wrap.Children.Add(IconBtn("", "Yinele (Ctrl+Y)", Redo));
        wrap.Children.Add(Sep());
        var ocr = new Button { Content = "OCR", ToolTip = L.T("Bu sayfada metin tanıma; taranmış sayfalarda seçim ve düzeltmeyi açar"), Padding = new Thickness(10, 3, 10, 3), Height = 30 };
        ocr.Click += async (_, _) => await OcrPageAsync(_page); wrap.Children.Add(ocr);
        wrap.Children.Add(Sep());
        wrap.Children.Add(BuildSearchBox());
        wrap.Children.Add(IconBtn("", L.T("Yapay zeka / metin / notlar panelini göster/gizle"), () => { var show = _sideCol.Width.Value == 0; _sideCol.Width = new GridLength(show ? 340 : 0); AppSettings.Shared.PdfSidePanel = show; try { AppSettings.Shared.Save(); } catch { } if (_fitWidthMode) Dispatcher.BeginInvoke(new Action(FitWidth), DispatcherPriority.Loaded); }));
        return bar;
    }

    // ------------------------------------------------------------------ opening and page layout

    Task<JsonElement> Call(string op, object args) => Task.Run(() => _worker.CallAsync(op, args, _life.Token));

    async Task OpenAsync()
    {
        _status.Text = L.T("PDF açılıyor…");
        JsonElement info;
        try { info = await Call("pdfs_open", new { path = _path }); }
        catch (Exception ex) when (ex.Message.Contains("assword", StringComparison.OrdinalIgnoreCase))
        {
            var pw = AskPassword(); if (pw == null) { _status.Text = L.T("Parola girilmedi."); return; }
            try { info = await Call("pdfs_open", new { path = _path, password = pw }); _password = pw; }
            catch (Exception ex2) { _status.Text = L.T("PDF açılamadı: ") + ex2.Message; return; }
        }
        catch (Exception ex) { if (!_life.IsCancellationRequested) _status.Text = L.T("PDF açılamadı: ") + ex.Message; return; }
        _pages = info.GetProperty("pages").GetInt32();
        int i = 0;
        foreach (var s in info.GetProperty("sizes").EnumerateArray())
        {
            var p = new PageState { Index = i++, W = s.GetProperty("w").GetDouble(), H = s.GetProperty("h").GetDouble(), BaseRotation = s.GetProperty("rotation").GetInt32() };
            BuildPage(p); _pageStates.Add(p);
            var thumb = new Image { Stretch = Stretch.Uniform };
            p.Thumb = thumb; ApplyPageGeometry(p);
            var cell = new StackPanel { Margin = new Thickness(6) };
            cell.Children.Add(new Border { Background = Brushes.White, BorderBrush = ThemeManager.Brush("BorderStrongBrush"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, Child = thumb, MinHeight = 40, MinWidth = 30 });
            cell.Children.Add(new TextBlock { Text = (p.Index + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), Foreground = ThemeManager.Brush("TextSecondaryBrush") });
            _thumbs.Items.Add(new ListBoxItem { Content = cell, HorizontalContentAlignment = HorizontalAlignment.Center });
        }
        _pageCount.Text = "/ " + _pages;
        if (_pages == 1) _thumbCol.Width = new GridLength(0);
        _loaded = true;
        UpdateLayout();
        // Single-page or landscape (technical drawing) documents fit the page, multi-page portrait documents fit the width.
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateLayout();
            if (_pages == 1 || _pageStates[0].ViewW > _pageStates[0].ViewH) { FitPage(); _fitPageMode = true; } else FitWidth();
            if (_zoom > 1.6) SetZoom(1.25, null);
        }, DispatcherPriority.Loaded);
        UpdatePageUi();
        for (int k = 0; k < Math.Min(_pages, 300); k++) _thumbQueue.Enqueue(k);
        _status.Text = L.F("{0} sayfa. Ctrl+tekerlek yakınlaştırır, orta tuş veya El aracıyla sürükleyerek kaydırabilirsiniz.", _pages);
        await LoadPageTextAsync(0);
    }

    string? _password;
    string? AskPassword()
    {
        var w = new Window { Title = L.T("Parola korumalı PDF"), Width = 360, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(16) }; w.Content = sp;
        sp.Children.Add(new TextBlock { Text = L.T("Bu PDF parola ile korunuyor. Parolayı girin:"), Margin = new Thickness(0, 0, 0, 8) });
        var pb = new PasswordBox(); sp.Children.Add(pb);
        var ok = new Button { Content = L.T("Aç"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80, Margin = new Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => w.DialogResult = true; sp.Children.Add(ok);
        w.Loaded += (_, _) => pb.Focus();
        return w.ShowDialog() == true ? pb.Password : null;
    }

    void BuildPage(PageState p)
    {
        p.Image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true, UseLayoutRounding = true };
        RenderOptions.SetBitmapScalingMode(p.Image, BitmapScalingMode.HighQuality);
        p.Marks = new Canvas { IsHitTestVisible = true };
        p.Selection = new Canvas { IsHitTestVisible = false };
        p.Temp = new Canvas { IsHitTestVisible = false };
        p.Overlay = new Canvas { Width = p.W, Height = p.H, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = false };
        p.Overlay.Children.Add(p.Marks); p.Overlay.Children.Add(p.Selection); p.Overlay.Children.Add(p.Temp);
        p.Sheet = new Grid { Background = Brushes.White };
        p.Sheet.Children.Add(p.Image); p.Sheet.Children.Add(p.Overlay);
        p.Frame = new Border { Child = p.Sheet, Margin = new Thickness(0, 0, 0, PageGap), HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1), BorderBrush = ThemeManager.Brush("BorderStrongBrush"),
            SnapsToDevicePixels = true };   // No shadow effect: WPF effects resample the content in an intermediate bitmap and blur it.
        p.Overlay.MouseLeftButtonDown += (s, e) => OnPageDown(p, e);
        p.Overlay.MouseMove += (s, e) => OnPageMove(p, e);
        p.Overlay.MouseLeftButtonUp += (s, e) => OnPageUp(p, e);
        p.Overlay.MouseRightButtonUp += (s, e) => OnPageContext(p, e);
        _stack.Children.Add(p.Frame);
        ApplyPageGeometry(p);
    }

    double Z => _zoom * PtToDip;

    void ApplyPageGeometry(PageState p)
    {
        // The page size is rounded to whole pixels; the drawing sits 1:1 with no resampling blur.
        var dpi = DeviceScale; p.Sheet.Width = Math.Round(p.W * Z * dpi) / dpi; p.Sheet.Height = Math.Round(p.H * Z * dpi) / dpi;
        p.Overlay.RenderTransform = new ScaleTransform(Z, Z);
        p.Sheet.LayoutTransform = p.Rotation == 0 ? Transform.Identity : new RotateTransform(p.Rotation);
        if (p.Thumb != null)
        {
            p.Thumb.LayoutTransform = new RotateTransform(p.Rotation);
            // The long edge of the unrotated image is 118 px; it still fits the box after rotation.
            p.Thumb.Width = double.NaN; p.Thumb.Height = double.NaN;
            if (p.W > p.H) p.Thumb.Width = 118; else p.Thumb.Height = 118;
        }
    }

    void RecomputeTops()
    {
        double y = _stack.Margin.Top;
        foreach (var p in _pageStates) { p.Top = y; y += p.ViewH * Z + 2 + PageGap; }
    }

    // ------------------------------------------------------------------ zoom

    void ApplyZoomText(string s)
    {
        if (s.StartsWith("Genişli") || s == L.T("Genişliğe sığdır")) { FitWidth(); return; }
        if (s.StartsWith("Sayfaya") || s == L.T("Sayfaya sığdır")) { FitPage(); return; }
        if (double.TryParse(s.Replace("%", "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) && v > 0) { _fitWidthMode = false; _fitPageMode = false; SetZoom(v / 100, null); }
    }

    void FitWidth()
    {
        if (_pageStates.Count == 0) return;
        // The vertical scroll bar appears once pages are laid out; its width is subtracted up front.
        var avail = Math.Max(200, _scroll.ActualWidth - SystemParameters.VerticalScrollBarWidth - 2) - _stack.Margin.Left - _stack.Margin.Right - 24;
        // Pages are centered on the widest page; in mixed portrait/landscape documents the widest page is fitted so no horizontal scrolling appears.
        var widest = _pageStates.Take(200).Max(p => p.ViewW);
        SetZoom(avail / (widest * PtToDip), null); _fitWidthMode = true; _fitPageMode = false;
    }

    void FitPage()
    {
        if (_pageStates.Count == 0) return;
        var p = _pageStates[Math.Clamp(_page, 0, _pages - 1)];
        var aw = Math.Max(200, _scroll.ActualWidth - (_pages > 1 ? SystemParameters.VerticalScrollBarWidth : 0) - 2) - _stack.Margin.Left - _stack.Margin.Right - 4;
        var ah = Math.Max(200, _scroll.ActualHeight - 2) - _stack.Margin.Top - _stack.Margin.Bottom - PageGap - 4;
        SetZoom(Math.Min(aw / (p.ViewW * PtToDip), ah / (p.ViewH * PtToDip)), null); _fitWidthMode = false; _fitPageMode = true;
        GoToPage(_page);
    }

    void StepZoom(int dir, Point? anchor)
    {
        _fitWidthMode = false; _fitPageMode = false;
        double next = dir > 0 ? ZoomSteps.FirstOrDefault(z => z > _zoom + 1e-3, ZoomSteps[^1]) : ZoomSteps.LastOrDefault(z => z < _zoom - 1e-3, ZoomSteps[0]);
        SetZoom(next, anchor);
    }

    /// <summary>anchor: mouse point relative to the scroll view; zooming keeps the content under this point fixed.</summary>
    void SetZoom(double zoom, Point? anchor)
    {
        zoom = Math.Clamp(zoom, .1, 8); if (Math.Abs(zoom - _zoom) < 1e-4 && _pageStates.All(p => Math.Abs(p.Sheet.Width - p.W * Z) < .5)) { UpdateZoomText(); return; }
        var a = anchor ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);
        var contentX = _scroll.HorizontalOffset + a.X; var contentY = _scroll.VerticalOffset + a.Y;
        var ratio = zoom / _zoom; _zoom = zoom;
        foreach (var p in _pageStates) ApplyPageGeometry(p);
        RecomputeTops();
        _stack.UpdateLayout(); _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(Math.Max(0, contentX * ratio - a.X));
        _scroll.ScrollToVerticalOffset(Math.Max(0, (contentY - _stack.Margin.Top) * ratio + _stack.Margin.Top - a.Y));
        UpdateZoomText(); ScheduleRender();
    }

    void UpdateZoomText()
    {
        var t = $"{Math.Round(_zoom * 100)}%";
        _zoomInfo.Text = L.T("Yakınlaştırma ") + t;
        if (!_zoomBox.IsKeyboardFocusWithin) { _zoomBox.SelectedItem = null; _zoomBox.Text = t; }
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true; _fitWidthMode = false; _fitPageMode = false;
        var factor = Math.Pow(1.0015, e.Delta);
        SetZoom(_zoom * factor, e.GetPosition(_scroll));
    }

    // ------------------------------------------------------------------ panning (hand tool / middle button)

    void OnPanDown(object sender, MouseButtonEventArgs e)
    {
        var hand = e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && (_tool == Tool.Hand || Keyboard.IsKeyDown(Key.Space)));
        if (!hand) return;
        if (e.OriginalSource is DependencyObject d && FindParent<ScrollBar>(d) != null) return;
        _panStart = e.GetPosition(_scroll); _panOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _scroll.CaptureMouse(); _scroll.Cursor = Cursors.SizeAll; e.Handled = true;
    }
    void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } s) return;
        var p = e.GetPosition(_scroll);
        _scroll.ScrollToHorizontalOffset(_panOffset.X - (p.X - s.X)); _scroll.ScrollToVerticalOffset(_panOffset.Y - (p.Y - s.Y));
        e.Handled = true;
    }
    void OnPanUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart == null) return;
        _panStart = null; _scroll.ReleaseMouseCapture(); _scroll.Cursor = _tool == Tool.Hand ? Cursors.Hand : null; e.Handled = true;
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null) { if (d is T t) return t; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return null;
    }

    // ------------------------------------------------------------------ gezinme

    void GoToPage(int i)
    {
        if (_pages == 0) return;
        i = Math.Clamp(i, 0, _pages - 1);
        RecomputeTops();
        _scroll.ScrollToVerticalOffset(Math.Max(0, _pageStates[i].Top - 8));
        _page = i; UpdatePageUi();
    }

    void UpdateCurrentPage()
    {
        if (!_loaded || _pageStates.Count == 0) return;
        RecomputeTops();
        var mid = _scroll.VerticalOffset + _scroll.ViewportHeight * .35;
        var cur = 0;
        for (int i = 0; i < _pageStates.Count; i++) { if (_pageStates[i].Top <= mid) cur = i; else break; }
        if (cur != _page) { _page = cur; UpdatePageUi(); _ = LoadPageTextAsync(cur); }
    }

    void UpdatePageUi()
    {
        if (!_pageBox.IsKeyboardFocusWithin) _pageBox.Text = (_page + 1).ToString();
        if (_thumbs.SelectedIndex != _page) { _thumbs.SelectedIndex = _page; if (_thumbs.SelectedItem != null) _thumbs.ScrollIntoView(_thumbs.SelectedItem); }
    }

    // ------------------------------------------------------------------ render queue

    void ScheduleRender() { if (_loaded) { _renderDebounce.Stop(); _renderDebounce.Start(); } }

    double DeviceScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>PDF points per screen pixel: the page is shown on screen at exactly this pixel size, 1:1.</summary>
    double ScreenScale => Z * DeviceScale;

    /// <summary>3× supersampling at small scale, 2× at medium scale (downscaled with Lanczos in the worker); not needed at large scale.</summary>
    double Supersample => ScreenScale < 1 ? 3 : ScreenScale < 2.5 ? 2 : 1.25;

    (int w, int h) TargetPixels(PageState p)
    {
        var s = ScreenScale; var w = (int)Math.Round(p.W * s); var h = (int)Math.Round(p.H * s);
        var cap = Math.Min(1.0, Math.Min(6000.0 / Math.Max(w, h), Math.Sqrt(24_000_000.0 / Math.Max(1.0, (double)w * h))));
        return ((int)Math.Max(1, w * cap), (int)Math.Max(1, h * cap));
    }

    void QueueVisible()
    {
        if (!_loaded) return;
        RecomputeTops();
        var top = _scroll.VerticalOffset - _scroll.ViewportHeight * .5; var bottom = _scroll.VerticalOffset + _scroll.ViewportHeight * 1.5;
        var visible = _pageStates.Where(p => p.Top + p.ViewH * Z >= top && p.Top <= bottom).OrderBy(p => Math.Abs(p.Index - _page)).ToList();
        foreach (var p in visible)
        {
            var target = TargetPixels(p).w / p.W;
            if (!p.Queued && (p.RenderedScale <= 0 || Math.Abs(p.RenderedScale - target) / target > .015))
            { p.Queued = true; _renderQueue.Enqueue(p.Index); }
        }
        // release images of far-away pages (memory)
        foreach (var p in _pageStates)
            if (p.Image.Source != null && (p.Top + p.ViewH * Z < top - _scroll.ViewportHeight * 4 || p.Top > bottom + _scroll.ViewportHeight * 4))
            { p.Image.Source = null; p.RenderedScale = 0; }
        _ = RenderLoopAsync();
    }

    async Task RenderLoopAsync()
    {
        if (_renderLoopRunning) return;
        _renderLoopRunning = true;
        try
        {
            while (!_life.IsCancellationRequested)
            {
                if (_renderQueue.Count > 0)
                {
                    var p = _pageStates[_renderQueue.Dequeue()]; p.Queued = false;
                    var (tw, th) = TargetPixels(p);
                    var r = await Call("pdfs_render", new { page = p.Index, px = new[] { tw, th }, ss = Supersample });
                    var bmp = await Task.Run(() => Decode(r.GetProperty("image").GetString()!));
                    p.Image.Source = bmp; p.RenderedScale = r.GetProperty("scale").GetDouble();
                    if (p.Marks.Children.Count > 0) RedrawMarks(p);
                    if (p.Thumb != null && !p.ThumbDone) { p.Thumb.Source = bmp; p.ThumbDone = true; }
                }
                else if (_thumbQueue.Count > 0)
                {
                    var p = _pageStates[_thumbQueue.Dequeue()];
                    if (p.ThumbDone) continue;
                    var tp = 118 * DeviceScale / Math.Max(p.W, p.H);
                    var r = await Call("pdfs_render", new { page = p.Index, px = new[] { (int)Math.Round(p.W * tp), (int)Math.Round(p.H * tp) }, ss = 4.0, format = "jpeg" });
                    p.Thumb!.Source = await Task.Run(() => Decode(r.GetProperty("image").GetString()!)); p.ThumbDone = true;
                }
                else break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_life.IsCancellationRequested) _status.Text = L.T("Sayfa çizilemedi: ") + ex.Message; }
        finally { _renderLoopRunning = false; }
    }

    static BitmapSource Decode(string b64)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(b64));
        var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
        return bmp;
    }

    // ------------------------------------------------------------------ rotation

    void RotatePages(IEnumerable<int> pages, int delta)
    {
        var list = pages.Where(i => i >= 0 && i < _pages).ToArray(); if (list.Length == 0) return;
        void Apply(int d) { foreach (var i in list) { var p = _pageStates[i]; p.UserRotation = ((p.UserRotation + d) % 360 + 360) % 360; ApplyPageGeometry(p); } RecomputeTops(); ScheduleRender(); MarkDirty(); }
        Apply(delta);
        PushUndo(() => Apply(-delta), () => Apply(delta), list.Length == 1 ? L.T("Sayfa döndürme") : L.T("Tüm sayfaları döndürme"));
        if (list.Length == 1) GoToPage(list[0]);
        _status.Text = list.Length == 1 ? L.F("Sayfa {0} döndürüldü; kaydedince PDF'ye yazılır.", list[0] + 1) : L.T("Tüm sayfalar döndürüldü; kaydedince PDF'ye yazılır.");
    }

    // ------------------------------------------------------------------ klavye

    void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0; var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (Keyboard.FocusedElement is TextBox tb && tb != _pageBox && !(ctrl && e.Key is Key.S)) { if (e.Key == Key.Escape && _inlineEditor != null) { CancelInline(); e.Handled = true; } return; }
        if (ctrl && shift && e.Key is Key.OemPlus or Key.Add) { RotatePages(new[] { _page }, 90); e.Handled = true; }
        else if (ctrl && shift && e.Key is Key.OemMinus or Key.Subtract) { RotatePages(new[] { _page }, -90); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add) { StepZoom(1, null); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { StepZoom(-1, null); e.Handled = true; }
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0) { FitWidth(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { CopySelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { SelectAllOnPage(_page); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { _ = SaveAsAsync(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { _searchBox.Focus(); _searchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F3) { NextHit(shift ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelectedObject(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; }
        else if (e.Key == Key.PageDown && !ctrl && _scroll.IsKeyboardFocusWithin == false) { GoToPage(_page + 1); e.Handled = true; }
        else if (e.Key == Key.PageUp && !ctrl && _scroll.IsKeyboardFocusWithin == false) { GoToPage(_page - 1); e.Handled = true; }
        else if (e.Key == Key.Home && ctrl) { GoToPage(0); e.Handled = true; }
        else if (e.Key == Key.End && ctrl) { GoToPage(_pages - 1); e.Handled = true; }
    }
}

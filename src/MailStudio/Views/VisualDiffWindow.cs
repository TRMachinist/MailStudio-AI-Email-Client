using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MailStudio.Services;
using MailStudio.Views.Calendar;
using Microsoft.Win32;

namespace MailStudio.Views;

/// <summary>
/// Old and new revision drawn on top of each other: red = only in the old revision, green = only in the new one,
/// grey = unchanged. PDF pages and DXF drawings are compared pixel by pixel in the same frame; STEP models face by face.
/// The changed regions are listed on the right; clicking one zooms to it.
/// </summary>
public sealed class VisualDiffWindow : Window
{
    readonly string _old, _new;
    readonly Func<object, CancellationToken, Task<JsonElement>> _run;
    readonly CancellationTokenSource _life = new();
    readonly Image _image = new() { Stretch = Stretch.None, SnapsToDevicePixels = true };
    readonly Canvas _marks = new() { IsHitTestVisible = false };
    readonly Grid _surface = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly ScaleTransform _zoom = new(1, 1);
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White };
    readonly ListBox _regions = new() { BorderThickness = new Thickness(0) };
    readonly TextBlock _summary = PimUi.Text("", 12, brush: "TextSecondaryBrush");
    readonly TextBlock _busy = PimUi.Text("", 13, brush: "TextMutedBrush");
    readonly TextBlock _pageText = PimUi.Text("", 12);
    readonly Button _prev, _next;
    int _page, _pages = 1;
    byte[]? _png;

    /// <summary>The last result (for the self-test): "identical", "regions", "added_faces"...</summary>
    public JsonElement? Result { get; private set; }

    public VisualDiffWindow(string oldPath, string newPath, Func<object, CancellationToken, Task<JsonElement>> run, string? title = null)
    {
        _old = oldPath; _new = newPath; _run = run;
        UiTranslator.SetSkip(this, true);
        Title = L.T("Görsel revizyon farkı") + " – " + (title ?? System.IO.Path.GetFileName(newPath));
        Width = 1180; Height = 800; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        // toolbar
        var bar = new WrapPanel { Margin = new Thickness(12, 10, 12, 6) };
        Button Btn(string text, RoutedEventHandler click, string? tip = null)
        {
            var b = new Button { Content = text, Height = 28, MinWidth = 34, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            b.SetResourceReference(StyleProperty, "RibbonCmd"); b.Click += click; bar.Children.Add(b); return b;
        }
        Btn(L.T("Sığdır"), (_, _) => Fit());
        Btn("100 %", (_, _) => SetZoom(1));
        Btn("+", (_, _) => SetZoom(_zoom.ScaleX * 1.25));
        Btn("−", (_, _) => SetZoom(_zoom.ScaleX / 1.25));
        _prev = Btn("◀", async (_, _) => await LoadAsync(_page - 1), L.T("Önceki sayfa"));
        _pageText.VerticalAlignment = VerticalAlignment.Center; _pageText.Margin = new Thickness(0, 0, 6, 4); bar.Children.Add(_pageText);
        _next = Btn("▶", async (_, _) => await LoadAsync(_page + 1), L.T("Sonraki sayfa"));
        Btn(L.T("PNG olarak kaydet…"), (_, _) => SavePng());
        bar.Children.Add(Legend(Color.FromRgb(214, 40, 40), L.T("yalnız eski revizyonda")));
        bar.Children.Add(Legend(Color.FromRgb(16, 150, 64), L.T("yeni / değişen")));
        bar.Children.Add(Legend(Color.FromRgb(70, 74, 82), L.T("aynı")));

        _surface.Children.Add(_image); _surface.Children.Add(_marks);
        _surface.LayoutTransform = _zoom;
        _scroll.Content = _surface;
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetZoom(_zoom.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15)); e.Handled = true;
        };
        var view = new Grid();
        view.Children.Add(_scroll);
        _busy.HorizontalAlignment = HorizontalAlignment.Center; _busy.VerticalAlignment = VerticalAlignment.Center;
        view.Children.Add(_busy);

        var side = new DockPanel { Width = 280, Margin = new Thickness(8, 0, 12, 12) };
        var sideHead = new StackPanel();
        sideHead.Children.Add(PimUi.Text(L.T("Değişen bölgeler"), 13, bold: true));
        _summary.TextWrapping = TextWrapping.Wrap; _summary.Margin = new Thickness(0, 4, 0, 8);
        sideHead.Children.Add(_summary);
        DockPanel.SetDock(sideHead, Dock.Top); side.Children.Add(sideHead);
        _regions.SelectionChanged += (_, _) => { if (_regions.SelectedItem is ListBoxItem { Tag: Rect r }) ZoomTo(r); };
        side.Children.Add(_regions);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var frame = new Border { Child = view, Margin = new Thickness(12, 0, 0, 12), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        body.Children.Add(frame);
        Grid.SetColumn(side, 1); body.Children.Add(side);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;
        Closed += (_, _) => _life.Cancel();
        Loaded += async (_, _) => await LoadAsync(0);
    }

    static FrameworkElement Legend(Color c, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 4, 4), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(c), RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 5, 0) });
        sp.Children.Add(PimUi.Text(text, 12));
        return sp;
    }

    /// <summary>Completes when the current comparison has been drawn (or failed).</summary>
    public Task Ready => _ready.Task;
    TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    async Task LoadAsync(int page)
    {
        if (page < 0 || page >= _pages) return;
        _busy.Text = L.T("Karşılaştırılıyor…"); _busy.Visibility = Visibility.Visible;
        _prev.IsEnabled = _next.IsEnabled = false;
        try
        {
            var r = await _run(new { old = _old, @new = _new, page }, _life.Token);
            Result = r;
            _png = Convert.FromBase64String(r.GetProperty("image").GetString()!);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(_png)) { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
            bmp.Freeze();
            _image.Source = bmp; _image.Width = bmp.PixelWidth; _image.Height = bmp.PixelHeight;
            _marks.Width = bmp.PixelWidth; _marks.Height = bmp.PixelHeight;
            var kind = r.GetProperty("kind").GetString();
            _page = page;
            _pages = r.TryGetProperty("pages", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : 1;
            var paged = kind == "pdf" && _pages > 1;
            _prev.Visibility = _next.Visibility = _pageText.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
            _pageText.Text = L.F("Sayfa {0} / {1}", _page + 1, _pages);
            _prev.IsEnabled = _page > 0; _next.IsEnabled = _page < _pages - 1;
            FillRegions(r, kind);
            _busy.Visibility = Visibility.Collapsed;
            Fit();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _busy.Text = L.T("Görsel fark oluşturulamadı: ") + ex.Message; _busy.TextWrapping = TextWrapping.Wrap; }
        finally { _ready.TrySetResult(); }
    }

    void FillRegions(JsonElement r, string? kind)
    {
        _regions.Items.Clear(); _marks.Children.Clear();
        if (r.GetProperty("identical").GetBoolean())
        {
            _summary.Text = kind == "step" ? L.T("İki modelin yüzeyleri aynı.") : L.T("Görünür fark yok: iki revizyon bu sayfada aynı çiziliyor.");
            return;
        }
        if (kind == "step")
        {
            _summary.Text = L.F("{0} yüzey yeni veya değişmiş (yeşil), {1} yüzey kaldırılmış (kırmızı, saydam). Eski {2}, yeni {3} yüzey.",
                r.GetProperty("added_faces").GetInt32(), r.GetProperty("removed_faces").GetInt32(), r.GetProperty("faces_old").GetInt32(), r.GetProperty("faces_new").GetInt32());
            foreach (var c in r.GetProperty("changes").EnumerateArray())
            {
                var added = c.GetProperty("state").GetString() == "added";
                var ctr = c.GetProperty("center_mm").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                _regions.Items.Add(new ListBoxItem
                {
                    Content = Row(added ? Color.FromRgb(16, 150, 64) : Color.FromRgb(214, 40, 40),
                        $"{(added ? L.T("Yeni") : L.T("Kaldırıldı"))}: {FaceType(c.GetProperty("type").GetString())}",
                        L.F("alan {0:0.##} mm² · merkez {1:0.##}, {2:0.##}, {3:0.##}", c.GetProperty("area_mm2").GetDouble(), ctr[0], ctr[1], ctr[2]))
                });
            }
            return;
        }
        var regions = r.GetProperty("regions").EnumerateArray().ToList();
        _summary.Text = L.F("{0} bölgede fark var. Bir bölgeye tıklayınca oraya yakınlaşır.", regions.Count)
                        + (kind == "pdf" && r.TryGetProperty("shift_px", out var sh) && (sh[0].GetInt32() != 0 || sh[1].GetInt32() != 0)
                            ? " " + L.F("Yeni sayfa {0}, {1} piksel kaydırılarak hizalandı.", sh[0].GetInt32(), sh[1].GetInt32()) : "");
        var n = 0;
        foreach (var g in regions)
        {
            var rect = new Rect(g.GetProperty("x").GetDouble(), g.GetProperty("y").GetDouble(), g.GetProperty("w").GetDouble(), g.GetProperty("h").GetDouble());
            var kindText = g.GetProperty("kind").GetString() switch { "added" => L.T("eklendi"), "removed" => L.T("kaldırıldı"), _ => L.T("değişti") };
            var color = g.GetProperty("kind").GetString() switch { "added" => Color.FromRgb(16, 150, 64), "removed" => Color.FromRgb(214, 40, 40), _ => Color.FromRgb(200, 120, 0) };
            n++;
            _regions.Items.Add(new ListBoxItem { Tag = rect, Content = Row(color, L.F("Bölge {0}: {1}", n, kindText), L.F("{0} × {1} px", (int)rect.Width, (int)rect.Height)) });
            var box = new Rectangle { Width = rect.Width + 12, Height = rect.Height + 12, Stroke = new SolidColorBrush(Color.FromArgb(170, 200, 120, 0)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 } };
            Canvas.SetLeft(box, rect.X - 6); Canvas.SetTop(box, rect.Y - 6); _marks.Children.Add(box);
            var label = new TextBlock { Text = n.ToString(), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(200, 120, 0)), Padding = new Thickness(4, 0, 4, 0) };
            Canvas.SetLeft(label, rect.X - 6); Canvas.SetTop(label, Math.Max(0, rect.Y - 24)); _marks.Children.Add(label);
        }
    }

    static string FaceType(string? t) => t switch
    {
        "Plane" => L.T("düzlem"), "Cylinder" => L.T("silindir"), "Cone" => L.T("koni"), "Sphere" => L.T("küre"), "Torus" => L.T("torus"),
        "BSplineSurface" or "BezierSurface" => L.T("serbest yüzey"), _ => t ?? ""
    };

    static FrameworkElement Row(Color c, string title, string detail)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        sp.Children.Add(new Rectangle { Width = 10, Height = 26, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 8, 0) });
        var t = new StackPanel();
        t.Children.Add(PimUi.Text(title, 12.5, bold: true));
        t.Children.Add(PimUi.Text(detail, 11.5, brush: "TextSecondaryBrush"));
        sp.Children.Add(t);
        return sp;
    }

    void SetZoom(double z)
    {
        z = Math.Clamp(z, 0.05, 8);
        _zoom.ScaleX = _zoom.ScaleY = z;
    }

    void Fit()
    {
        if (_image.Source is not BitmapSource b) return;
        UpdateLayout();
        var w = Math.Max(50, _scroll.ViewportWidth - 4); var h = Math.Max(50, _scroll.ViewportHeight - 4);
        if (_scroll.ViewportWidth <= 0) { w = Math.Max(50, ActualWidth - 330); h = Math.Max(50, ActualHeight - 140); }
        SetZoom(Math.Min(w / b.PixelWidth, h / b.PixelHeight));
    }

    void ZoomTo(Rect r)
    {
        var w = Math.Max(50, _scroll.ViewportWidth); var h = Math.Max(50, _scroll.ViewportHeight);
        var z = Math.Clamp(Math.Min(w / (r.Width + 160), h / (r.Height + 160)), 0.2, 3);
        SetZoom(z); UpdateLayout();
        _scroll.ScrollToHorizontalOffset(Math.Max(0, (r.X + r.Width / 2) * z - w / 2));
        _scroll.ScrollToVerticalOffset(Math.Max(0, (r.Y + r.Height / 2) * z - h / 2));
    }

    void SavePng()
    {
        if (_png == null) return;
        var d = new SaveFileDialog
        {
            Filter = "PNG|*.png",
            FileName = System.IO.Path.GetFileNameWithoutExtension(_new) + " " + L.T("görsel fark") + (_pages > 1 ? $" p{_page + 1}" : "") + ".png"
        };
        if (d.ShowDialog(this) == true)
            try { File.WriteAllBytes(d.FileName, _png); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.ProductName); }
    }

    /// <summary>Saves the overlay image without a dialog (used by the self-test).</summary>
    public void SavePngTo(string path) { if (_png != null) File.WriteAllBytes(path, _png); }
}

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// DWFx / XPS viewer (Windows' built-in XPS engine): pages, zoom, text search, printing.
/// There is no open-source renderer for classic DWF (Autodesk's closed W2D vector stream); the preview images in the package are shown.
/// </summary>
public sealed class XpsViewerWindow : Window
{
    readonly string _path; XpsDocument? _xps;

    public XpsViewerWindow(string path)
    {
        _path = path;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        Title = Path.GetFileName(path) + (ext == ".dwf" ? " — DWF" : ext == ".dwfx" ? " — DWFx" : " — XPS");
        Width = 1200; Height = 860; MinWidth = 640; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel(); Content = root;
        var bar = new Border { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var open = new Button { Content = L.T("Varsayılan programda aç"), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right };
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { status.Text = ex.Message; } };
        var bd = new DockPanel(); DockPanel.SetDock(open, Dock.Right); bd.Children.Add(open); bd.Children.Add(status); bar.Child = bd;
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        try
        {
            if (ext == ".dwf" && !IsXps(path)) { root.Children.Add(DwfPreview(path, status)); return; }
            _xps = new XpsDocument(path, FileAccess.Read);
            var viewer = new DocumentViewer { Document = _xps.GetFixedDocumentSequence() };
            root.Children.Add(viewer);
            status.Text = L.T("Ctrl+tekerlek yakınlaştırır; arama kutusu ve yazdırma görüntüleyicinin araç çubuğundadır.");
            Loaded += (_, _) => viewer.FitToWidth();
        }
        catch (Exception ex)
        {
            root.Children.Add(new TextBlock { Text = L.T("Belge açılamadı: ") + ex.Message, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap });
        }
        Closed += (_, _) => _xps?.Close();
    }

    static bool IsXps(string path)
    {
        try { using var z = ZipFile.OpenRead(path); return z.Entries.Any(e => e.FullName.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>Classic DWF: preview/texture images in the zip package; the vector content (W2D/W3D) cannot be drawn with open source.</summary>
    static UIElement DwfPreview(string path, TextBlock status)
    {
        var images = new List<BitmapSource>(); var sections = new List<string>();
        try
        {
            using var z = ZipFile.OpenRead(path);
            foreach (var e in z.Entries)
            {
                var n = e.FullName.ToLowerInvariant();
                if (n.EndsWith(".w2d") || n.EndsWith(".w3d")) sections.Add(e.FullName);
                if ((n.EndsWith(".png") || n.EndsWith(".jpg") || n.EndsWith(".jpeg") || n.EndsWith(".bmp")) && e.Length < 30_000_000)
                {
                    using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;
                    var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = ms; b.EndInit(); b.Freeze(); images.Add(b);
                }
            }
        }
        catch { status.Text = L.T("Bu DWF sürümü (zip olmayan eski DWF) okunamıyor. Varsayılan programda açabilirsiniz."); return new Grid(); }
        status.Text = L.F("Klasik DWF: {0} vektör bölüm (W2D/W3D). Bu Autodesk biçimi için açık kaynak çizici yoktur; paketteki {1} önizleme resmi gösteriliyor. Tam görünüm için Autodesk Design Review / DWG TrueView (ücretsiz) veya DWFx olarak yayımlayın.", sections.Count, images.Count);
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (var img in images.OrderByDescending(i => i.PixelWidth * i.PixelHeight))
            panel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Left, Child = new Image { Source = img, Stretch = Stretch.Uniform, MaxWidth = Math.Min(1400, img.PixelWidth * 2) } });
        if (images.Count == 0) panel.Children.Add(new TextBlock { Text = L.T("Pakette önizleme resmi yok."), Foreground = Brushes.Gray });
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

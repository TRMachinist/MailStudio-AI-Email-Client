using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailStudio.Views.Editors;

/// <summary>
/// Word editor: the document body is edited in page view inside WebView2 (text, headings, bold/italic/underline,
/// color, highlight, alignment, lists, indent, table, image, link). Saving goes to a copy of the source document;
/// headers/footers, styles and page setup are kept. The attachment itself is never written.
/// </summary>
public sealed class WordEditorWindow : Window
{
    readonly string _path, _displaySource; string? _savePath;
    readonly WebView2 _web = new();
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox _block = new() { Width = 120, ToolTip = L.T("Paragraf stili") }, _size = new() { Width = 62, IsEditable = true, ToolTip = L.T("Yazı boyutu (pt)") };
    readonly Border _warning = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(12, 6, 12, 6) };
    DocxHtml.Result? _doc; bool _dirty, _ready, _closed;

    public WordEditorWindow(string path, string? displaySource = null)
    {
        _path = path; _displaySource = displaySource ?? path;
        Title = Path.GetFileName(_displaySource) + L.T(" — Word düzenleyici");
        Width = 1180; Height = 900; MinWidth = 720; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid();
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new() { Height = h });
        Content = root;
        root.Children.Add(BuildToolbar());
        _warning.Background = ThemeManager.Brush("StatusWarningBgBrush") ?? Brushes.LightYellow; _warning.BorderBrush = ThemeManager.Brush("BorderSubtleBrush"); _warning.BorderThickness = new Thickness(0, 0, 0, 1);
        Grid.SetRow(_warning, 1); root.Children.Add(_warning);
        _web.DefaultBackgroundColor = ThemeManager.IsDark ? System.Drawing.Color.FromArgb(0x20, 0x20, 0x20) : System.Drawing.Color.FromArgb(0xE9, 0xE9, 0xE9);
        Grid.SetRow(_web, 2); root.Children.Add(_web);
        var status = new Border { Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var sd = new DockPanel(); status.Child = sd; DockPanel.SetDock(_info, Dock.Right); _info.Foreground = ThemeManager.Brush("TextSecondaryBrush"); sd.Children.Add(_info); sd.Children.Add(_status);
        Grid.SetRow(status, 3); root.Children.Add(status);
        PreviewKeyDown += (_, e) => { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) { _ = SaveAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; } };
        Loaded += async (_, _) => await LoadAsync();
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _web.Dispose(); };
    }

    UIElement BuildToolbar()
    {
        var bar = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var w = new WrapPanel(); bar.Child = w;
        void B(string glyph, string tip, Action a, string? text = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = sp, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(7, 3, 7, 3), MinHeight = 28, Focusable = false };
            if (TryFindResource("SubtleButton") is Style s) b.Style = s;
            b.Click += (_, _) => a(); w.Children.Add(b);
        }
        void Sep() => w.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), Background = ThemeManager.Brush("BorderSubtleBrush"), VerticalAlignment = VerticalAlignment.Center });
        void Cmd(string glyph, string tip, string command, string? arg = null) => B(glyph, tip, () => Exec($"cmd({JsonSerializer.Serialize(command)},{(arg == null ? "null" : JsonSerializer.Serialize(arg))})"));
        B("", L.T("Kaydet (Ctrl+S)"), () => _ = SaveAsync(false), L.T("Kaydet"));
        B("", L.T("Farklı kaydet (Ctrl+Shift+S)"), () => _ = SaveAsync(true));
        Sep();
        Cmd("", L.T("Geri al (Ctrl+Z)"), "undo"); Cmd("", "Yinele (Ctrl+Y)", "redo");
        Sep();
        foreach (var (n, tag) in new[] { ("Normal", "P"), (L.T("Başlık 1"), "H1"), (L.T("Başlık 2"), "H2"), (L.T("Başlık 3"), "H3") }) _block.Items.Add(new ComboBoxItem { Content = n, Tag = tag });
        _block.SelectedIndex = 0; _block.Focusable = false; _block.Margin = new Thickness(2, 0, 4, 0);
        _block.SelectionChanged += (_, _) => { if (_ready && _block.IsDropDownOpen == false && _block.SelectedItem is ComboBoxItem { Tag: string tag }) Exec($"cmd('formatBlock','{tag}')"); };
        _block.DropDownClosed += (_, _) => { if (_block.SelectedItem is ComboBoxItem { Tag: string tag }) Exec($"cmd('formatBlock','{tag}')"); };
        w.Children.Add(_block);
        foreach (var s in new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36" }) _size.Items.Add(s);
        _size.Text = "11"; _size.Margin = new Thickness(0, 0, 4, 0);
        _size.DropDownClosed += (_, _) => SetSize(); _size.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SetSize(); e.Handled = true; } };
        w.Children.Add(_size);
        Cmd("", L.T("Kalın (Ctrl+B)"), "bold"); Cmd("", L.T("İtalik (Ctrl+I)"), "italic"); Cmd("", L.T("Altı çizili (Ctrl+U)"), "underline"); Cmd("", L.T("Üstü çizili"), "strikeThrough");
        Cmd("", L.T("Üst simge"), "superscript"); Cmd("", L.T("Alt simge"), "subscript");
        ColorButton(w, "", L.T("Yazı rengi"), "foreColor", new[] { "#000000", "#C00000", "#ED7D31", "#00B050", "#0070C0", "#7030A0", "#808080" });
        ColorButton(w, "", L.T("Vurgulama"), "hiliteColor", new[] { "transparent", "#FFFF00", "#00FF00", "#00FFFF", "#FF00FF", "#FFC000", "#D9D9D9" });
        Sep();
        Cmd("", L.T("Sola hizala"), "justifyLeft"); Cmd("", L.T("Ortala"), "justifyCenter"); Cmd("", L.T("Sağa hizala"), "justifyRight"); Cmd("", L.T("İki yana yasla"), "justifyFull");
        Cmd("", L.T("Madde işaretli liste"), "insertUnorderedList"); Cmd("", L.T("Numaralı liste"), "insertOrderedList");
        Cmd("", L.T("Girintiyi azalt"), "outdent"); Cmd("", L.T("Girintiyi artır"), "indent");
        Sep();
        B("", L.T("Tablo ekle"), InsertTable); B("", L.T("Resim ekle"), InsertImage); B("", L.T("Bağlantı ekle"), InsertLink);
        Cmd("", L.T("Biçimi temizle"), "removeFormat");
        Sep();
        B("", "Bul (Ctrl+F)", () => Exec(L.T("window.find && window.find(prompt('Aranacak metin:')||'')")));
        B("", L.T("Word'de aç (tam düzen ve özellikler)"), () => Shell(_savePath ?? _displaySource), L.T("Word'de aç"));
        return bar;
    }

    void ColorButton(WrapPanel host, string glyph, string tip, string command, string[] colors)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 });
        sp.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 8, Margin = new Thickness(3, 4, 0, 0) });
        var b = new Button { Content = sp, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(7, 3, 7, 3), MinHeight = 28, Focusable = false };
        if (TryFindResource("SubtleButton") is Style st) b.Style = st;
        var m = new ContextMenu();
        foreach (var c in colors)
        {
            var mi = new MenuItem { Header = c == "transparent" ? L.T("Yok") : c, Icon = new Border { Width = 14, Height = 14, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = c == "transparent" ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(c)! } };
            mi.Click += (_, _) => Exec($"cmd('{command}','{c}')"); m.Items.Add(mi);
        }
        b.Click += (_, _) => { m.PlacementTarget = b; m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; m.IsOpen = true; };
        host.Children.Add(b);
    }

    void SetSize()
    {
        if (!double.TryParse(_size.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pt) || pt < 4 || pt > 96) return;
        Exec($"fontSizePt({pt.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    async void Exec(string js) { if (_ready && !_closed) { try { await _web.ExecuteScriptAsync(js); _web.Focus(); } catch { } } }

    async Task LoadAsync()
    {
        _status.Text = L.T("Belge açılıyor…");
        try { _doc = await Task.Run(() => DocxHtml.Read(_path)); }
        catch (Exception ex) { _status.Text = L.T("Belge okunamadı: ") + ex.Message; return; }
        if (_doc.Warnings.Count > 0)
        {
            _warning.Child = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("TextPrimaryBrush"),
                Text = L.F("Bu belgede {0} var. Bu düzenleyici gövde metnini düzenler; kaydedince bu öğeler sadeleşebilir veya kaybolabilir. Tam düzen için \"Word'de aç\"ı kullanın. Kayıt her zaman ayrı dosyaya yapılır.", string.Join(", ", _doc.Warnings)) };
            _warning.Visibility = Visibility.Visible;
        }
        try
        {
            await _web.EnsureCoreWebView2Async(await MailHtml.EnvAsync());
            if (_closed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true; core.Settings.IsStatusBarEnabled = false; core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("data:") && !a.Uri.StartsWith("about:")) { a.Cancel = true; if (a.Uri.StartsWith("http")) Shell(a.Uri); } };
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.WebMessageReceived += (_, a) =>
            {
                var msg = a.TryGetWebMessageAsString();
                if (msg == "dirty") MarkDirty();
                else if (msg.StartsWith("info:")) { var p = msg[5..].Split('|'); _info.Text = p.Length == 2 ? L.F("{0} sözcük • {1} karakter", p[0], p[1]) : msg[5..]; }
                else if (msg.StartsWith("state:")) { var parts = msg[6..].Split('|'); if (parts.Length == 2) { foreach (ComboBoxItem it in _block.Items) if ((string)it.Tag == parts[0]) { if (!_block.IsDropDownOpen) _block.SelectedItem = it; } if (!_size.IsKeyboardFocusWithin) _size.Text = parts[1]; } }
            };
            _web.NavigateToString(Page(_doc));
            _web.NavigationCompleted += (_, _) => { _ready = true; _status.Text = L.T("Düzenleyebilirsiniz. Kaydet ilk seferde yeni dosya sorar; ek dosyası değişmez."); _web.Focus(); };
        }
        catch (Exception ex) { _status.Text = L.T("Düzenleyici açılamadı: ") + ex.Message; }
    }

    static string Page(DocxHtml.Result d)
    {
        var dark = ThemeManager.IsDark;
        var width = Math.Clamp(d.PageWidthPx, 400, 1400);
        return $$"""
<!doctype html><html><head><meta charset="utf-8"><style>
html{background:{{(dark ? "#202020" : "#E9E9E9")}}}
body{margin:0;padding:24px 0 60px}
#doc{box-sizing:border-box;width:{{width:0}}px;min-height:{{width * 1.414:0}}px;margin:0 auto;background:#fff;color:#000;
 padding:{{d.Margins.Top:0}}px {{d.Margins.Right:0}}px {{d.Margins.Bottom:0}}px {{d.Margins.Left:0}}px;box-shadow:0 1px 4px rgba(0,0,0,.25);
 font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:11pt;line-height:1.25;outline:none;word-wrap:break-word}
#doc p{margin:0 0 6pt} #doc h1{font-size:16pt;margin:12pt 0 6pt;color:#1F3864} #doc h2{font-size:13pt;margin:10pt 0 4pt;color:#2F5496} #doc h3{font-size:12pt;margin:8pt 0 4pt;color:#1F3864}
#doc table{border-collapse:collapse;margin:4pt 0} #doc td,#doc th{border:1px solid #999;padding:3px 6px;vertical-align:top;min-width:30px}
#doc img{max-width:100%} #doc br[data-page]{display:block;border-top:1px dashed #bbb;margin:8px 0;content:''}
#doc a{color:#0563C1}
</style></head><body><div id="doc" contenteditable="true" spellcheck="true" lang="tr">{{d.Html}}</div>
<script>
const doc=document.getElementById('doc');
document.execCommand('styleWithCSS',false,true);
document.execCommand('defaultParagraphSeparator',false,'p');
let dirty=false;
doc.addEventListener('input',()=>{ if(!dirty){dirty=true;} chrome.webview.postMessage('dirty'); count(); });
function count(){ const t=doc.innerText||''; const w=(t.match(/\S+/g)||[]).length; chrome.webview.postMessage('info:'+w+'|'+t.length); }
function cmd(c,a){ doc.focus(); if(c==='hiliteColor'&&a==='transparent'){document.execCommand('hiliteColor',false,'transparent');} else document.execCommand(c,false,a); chrome.webview.postMessage('dirty'); }
function fontSizePt(pt){ doc.focus(); const sel=window.getSelection(); if(!sel.rangeCount||sel.isCollapsed) return;
  document.execCommand('fontSize',false,'7');
  doc.querySelectorAll('font[size="7"]').forEach(f=>{const s=document.createElement('span');s.style.fontSize=pt+'pt';s.innerHTML=f.innerHTML;f.replaceWith(s);});
  doc.querySelectorAll('span[style*="xxx-large"]').forEach(s=>s.style.fontSize=pt+'pt');
  chrome.webview.postMessage('dirty'); }
function insertHtml(h){ doc.focus(); document.execCommand('insertHTML',false,h); chrome.webview.postMessage('dirty'); }
document.addEventListener('selectionchange',()=>{ const s=window.getSelection(); if(!s.rangeCount) return; let n=s.anchorNode; if(n&&n.nodeType===3) n=n.parentElement; if(!n||!doc.contains(n)) return;
  const block=n.closest('h1,h2,h3,p,li,td'); const tag=block?block.tagName:'P'; const fs=getComputedStyle(n).fontSize; const pt=Math.round(parseFloat(fs)*0.75*2)/2;
  chrome.webview.postMessage('state:'+(['H1','H2','H3'].includes(tag)?tag:'P')+'|'+pt); });
doc.addEventListener('keydown',e=>{ if(e.key==='Tab'){ e.preventDefault(); document.execCommand(e.shiftKey?'outdent':'indent'); } });
doc.addEventListener('click',e=>{ const a=e.target.closest('a'); if(a&&e.ctrlKey){ e.preventDefault(); location.href=a.href; } });
count();
</script></body></html>
""";
    }

    void InsertTable()
    {
        var w = new Window { Title = L.T("Tablo ekle"), Width = 260, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(14) };
        var rows = new TextBox { Text = "3" }; var cols = new TextBox { Text = "3" };
        sp.Children.Add(new TextBlock { Text = L.T("Satır") }); sp.Children.Add(rows); sp.Children.Add(new TextBlock { Text = L.T("Sütun"), Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(cols);
        var ok = new Button { Content = L.T("Ekle"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), MinWidth = 80 }; ok.Click += (_, _) => w.DialogResult = true; sp.Children.Add(ok);
        w.Content = sp;
        if (w.ShowDialog() != true || !int.TryParse(rows.Text, out var r) || !int.TryParse(cols.Text, out var c) || r < 1 || c < 1 || r > 200 || c > 30) return;
        var html = "<table style=\"border-collapse:collapse;width:100%\">" + string.Concat(Enumerable.Range(0, r).Select(_ => "<tr>" + string.Concat(Enumerable.Range(0, c).Select(_ => "<td style=\"border:1px solid #999;padding:3px 6px\"><br></td>")) + "</tr>")) + "</table><p><br></p>";
        Exec($"insertHtml({JsonSerializer.Serialize(html)})");
    }

    void InsertImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Resimler|*.png;*.jpg;*.jpeg;*.gif;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        var bytes = File.ReadAllBytes(dlg.FileName); if (bytes.Length > 15_000_000) { _status.Text = L.T("Resim çok büyük (15 MB üstü)."); return; }
        var ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant(); if (ext == "jpg") ext = "jpeg";
        int width = 0; try { var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri(dlg.FileName)); width = Math.Min(600, bmp.PixelWidth); } catch { }
        Exec($"insertHtml({JsonSerializer.Serialize($"<img src=\"data:image/{ext};base64,{Convert.ToBase64String(bytes)}\"{(width > 0 ? $" width=\"{width}\"" : "")}>")})");
    }

    void InsertLink()
    {
        var w = new Window { Title = L.T("Bağlantı"), Width = 380, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(14) }; var url = new TextBox { Text = "https://" }; sp.Children.Add(new TextBlock { Text = L.T("Adres (seçili metne bağlanır)") }); sp.Children.Add(url);
        var ok = new Button { Content = L.T("Ekle"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), MinWidth = 80 }; ok.Click += (_, _) => w.DialogResult = true; sp.Children.Add(ok);
        w.Content = sp; w.Loaded += (_, _) => { url.Focus(); url.CaretIndex = url.Text.Length; };
        if (w.ShowDialog() != true) return;
        var u = url.Text.Trim(); if (!(u.StartsWith("http://") || u.StartsWith("https://") || u.StartsWith("mailto:"))) { _status.Text = L.T("Yalnızca http(s) ve mailto bağlantıları eklenir."); return; }
        Exec($"cmd('createLink',{JsonSerializer.Serialize(u)})");
    }

    void MarkDirty() { if (_dirty) return; _dirty = true; Title = "● " + Path.GetFileName(_savePath ?? _displaySource) + L.T(" — Word düzenleyici"); }

    public async Task<string?> GetHtmlAsync()
    {
        if (!_ready) return null;
        var json = await _web.ExecuteScriptAsync("document.getElementById('doc').innerHTML");
        return JsonSerializer.Deserialize<string>(json);
    }

    public async Task<bool> SaveAsync(bool forceDialog, string? path = null)
    {
        path ??= _savePath;
        if (path == null || forceDialog)
        {
            var ext = Path.GetExtension(_path).ToLowerInvariant() == ".docm" ? ".docm" : ".docx";
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = L.T("Word belgesini kaydet"), Filter = L.T("Word belgesi (*") + ext + ")|*" + ext, FileName = Path.GetFileNameWithoutExtension(_displaySource) + ext, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AddExtension = true };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            _status.Text = L.T("Kaydediliyor…");
            var html = await GetHtmlAsync() ?? throw new InvalidOperationException(L.T("Düzenleyici hazır değil."));
            var dest = path; var src = _savePath != null && File.Exists(_savePath) ? _savePath : _path;
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(_displaySource), StringComparison.OrdinalIgnoreCase) && dest != _savePath)
            { MessageBox.Show(this, L.T("Ek dosyasının üzerine yazılmaz; farklı bir ad seçin."), L.T("Word düzenleyici")); return false; }
            await DocxHtml.WriteAsync(src, dest, html);
            _savePath = dest; _dirty = false; Title = Path.GetFileName(dest) + L.T(" — Word düzenleyici");
            _status.Text = L.T("Kaydedildi: ") + dest;
            return true;
        }
        catch (Exception ex) { _status.Text = L.T("Kaydedilemedi: ") + ex.Message; if (IsVisible) MessageBox.Show(this, L.T("Kaydedilemedi:\n") + ex.Message, L.T("Word düzenleyici"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    bool _closeAfterSave;
    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty || _closeAfterSave) return;
        var a = MessageBox.Show(this, L.T("Kaydedilmemiş değişiklikler var. Kaydedilsin mi?"), L.T("Word düzenleyici"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (a == MessageBoxResult.No) return;
        e.Cancel = true;
        if (a == MessageBoxResult.Yes && await SaveAsync(false)) { _closeAfterSave = true; Close(); }
    }

    static void Shell(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }
}

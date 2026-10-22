using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using MailStudio.Services;
using MimeKit;

namespace MailStudio.Views;

/// <summary>
/// General document viewer: Office and formats with a system previewer (Word, Excel, PowerPoint, Outlook .msg, Visio…),
/// code / text / CNC programs (line numbers, highlighting, folding, search), CSV tables, ZIP contents and .eml messages.
/// The attachment file is never changed in place; edited text is written only with Save as.
/// </summary>
public sealed class DocumentViewerWindow : Window
{
    public enum Kind { Preview, Code, Csv, Zip, Eml }

    public static readonly string[] OfficeExtensions = { ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".xls", ".xlsx", ".xlsm", ".xlsb", ".xltx", ".ods", ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp", ".msg", ".vsd", ".vsdx", ".pub", ".one" };
    public static readonly string[] NcExtensions = { ".nc", ".tap", ".gcode", ".ngc", ".mpf", ".spf", ".cnc", ".iso", ".eia", ".h", ".min", ".ptp", ".anc", ".pim", ".prg", ".ncp" };
    public static readonly string[] CodeExtensions =
    {
        ".txt", ".log", ".ini", ".cfg", ".conf", ".config", ".json", ".xml", ".xaml", ".csproj", ".props", ".targets", ".resx", ".yml", ".yaml", ".toml", ".md", ".markdown",
        ".cs", ".vb", ".fs", ".py", ".pyw", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".java", ".kt", ".c", ".cpp", ".cc", ".hpp", ".hxx", ".rs", ".go", ".php", ".rb", ".lua", ".swift",
        ".sql", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".css", ".scss", ".less", ".html", ".htm", ".svg", ".vue", ".tex", ".bas", ".lsp", ".scr", ".mac", ".gpp", ".pst2", ".tcl", ".reg", ".inf", ".srt", ".properties", ".env", ".gitignore", ".editorconfig",
    };
    public static readonly string[] CsvExtensions = { ".csv", ".tsv", ".tab" };

    readonly string _path; readonly Kind _kind;
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly WrapPanel _tools = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly ContentControl _body = new();
    TextEditor? _editor; Encoding _encoding = new UTF8Encoding(false); FoldingManager? _folding; PreviewHandlerHost? _preview;
    Microsoft.Web.WebView2.Wpf.WebView2? _web;

    public static Kind? Classify(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".eml") return Kind.Eml;
        if (ext is ".zip" or ".rar" or ".7z") return Kind.Zip;
        if (CsvExtensions.Contains(ext)) return Kind.Csv;
        if (CodeExtensions.Contains(ext) || NcExtensions.Contains(ext)) return Kind.Code;
        if (OfficeExtensions.Contains(ext)) return Kind.Preview;
        if (PreviewHandlerHost.FindHandler(ext) != null) return Kind.Preview;
        if (LooksLikeText(path)) return Kind.Code;
        return null;
    }

    static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path); var buf = new byte[Math.Min(8192, fs.Length)]; var n = fs.Read(buf, 0, buf.Length);
            if (n == 0) return true;
            if (n >= 2 && (buf[0] == 0xFF && buf[1] == 0xFE || buf[0] == 0xFE && buf[1] == 0xFF)) return true;
            int ctrl = 0; for (int i = 0; i < n; i++) { if (buf[i] == 0) return false; if (buf[i] < 9 || buf[i] > 13 && buf[i] < 32) ctrl++; }
            return ctrl < n / 50;
        }
        catch { return false; }
    }

    public DocumentViewerWindow(string path, Kind kind)
    {
        _path = path; _kind = kind;
        Title = Path.GetFileName(path) + " — " + KindName(kind, path);
        Width = 1100; Height = 800; MinWidth = 560; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;

        var bar = new Border { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var dock = new DockPanel(); bar.Child = dock;
        var right = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(Btn("", L.T("Varsayılan programda aç"), () => Shell(path)));
        right.Children.Add(Btn("", L.T("Klasörde göster"), () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })));
        right.Children.Add(Btn("", L.T("Kopyasını kaydet…"), SaveCopy));
        dock.Children.Add(right);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = KindIcon(kind, path), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("IconBrush") });
        title.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
        DockPanel.SetDock(title, Dock.Left); dock.Children.Add(title);
        dock.Children.Add(_tools);
        root.Children.Add(bar);

        Grid.SetRow(_body, 1); root.Children.Add(_body);
        var status = new Border { Padding = new Thickness(12, 4, 12, 4), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var sd = new DockPanel(); status.Child = sd; DockPanel.SetDock(_info, Dock.Right); _info.Foreground = ThemeManager.Brush("TextSecondaryBrush");
        sd.Children.Add(_info); sd.Children.Add(_status);
        Grid.SetRow(status, 2); root.Children.Add(status);
        try { _info.Text = Size(new FileInfo(path).Length); } catch { }

        switch (kind)
        {
            case Kind.Preview: BuildPreview(); break;
            case Kind.Code: BuildCode(); break;
            case Kind.Csv: BuildCsv(); break;
            case Kind.Zip: BuildZip(); break;
            case Kind.Eml: BuildEml(); break;
        }
        Closed += (_, _) => { _preview?.Dispose(); _web?.Dispose(); };
    }

    static string KindName(Kind k, string path) => k switch
    {
        Kind.Preview => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".rtf" or ".odt" => L.T("Word görüntüleyici"),
            ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".xltx" or ".ods" => L.T("Excel görüntüleyici"),
            ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".odp" => L.T("PowerPoint görüntüleyici"),
            ".msg" => L.T("Outlook iletisi"), _ => L.T("Önizleme"),
        },
        Kind.Code => NcExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ? L.T("CNC program görüntüleyici") : L.T("Kod / metin görüntüleyici"),
        Kind.Csv => L.T("CSV tablo"), Kind.Zip => Path.GetExtension(path).ToLowerInvariant() switch { ".rar" => L.T("RAR arşivi"), ".7z" => L.T("7z arşivi"), _ => L.T("ZIP arşivi") }, Kind.Eml => L.T("E-posta iletisi"), _ => L.T("Görüntüleyici"),
    };

    static string KindIcon(Kind k, string path) => k switch { Kind.Code => "", Kind.Csv => "", Kind.Zip => "", Kind.Eml => "", _ => "" };

    Button Btn(string glyph, string tip, Action run, string? text = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, ToolTip = tip, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(8, 3, 8, 3), MinHeight = 28 };
        if (TryFindResource("SubtleButton") is Style s) b.Style = s;
        b.Click += (_, _) => run(); return b;
    }

    ToggleButton Toggle(string glyph, string text, string tip, bool on, Action<bool> run)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var t = new ToggleButton { Content = sp, ToolTip = tip, IsChecked = on, Margin = new Thickness(2, 0, 2, 0) };
        if (TryFindResource("MsSegmentToggleButtonStyle") is Style s) t.Style = s;
        t.Checked += (_, _) => run(true); t.Unchecked += (_, _) => run(false); return t;
    }

    static readonly string[] Scripts = { ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".mjs", ".wsf", ".hta", ".reg", ".sh", ".py", ".pyw", ".lnk", ".url", ".inf" };

    /// <summary>Script files would run on double-click, so they open in Notepad instead of the default program.</summary>
    void Shell(string p)
    {
        try
        {
            if (File.Exists(p) && Scripts.Contains(Path.GetExtension(p).ToLowerInvariant())) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{p}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    void SaveCopy()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(_path), Filter = L.T("Tüm dosyalar|*.*"), InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (_editor != null && !_editor.IsReadOnly) File.WriteAllText(dlg.FileName, _editor.Text, _encoding);
            else File.Copy(_path, dlg.FileName, true);
            _status.Text = L.T("Kaydedildi: ") + dlg.FileName;
        }
        catch (Exception ex) { _status.Text = L.T("Kaydedilemedi: ") + ex.Message; }
    }

    static string Size(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:0.#} KB" : $"{b / 1048576.0:0.#} MB";

    // ------------------------------------------------------------------ Office / system previewer

    void BuildPreview()
    {
        if (OfficeConverter.AppFor(Path.GetExtension(_path)) is { } app)
        {
            // Excel sheets open directly in the editor; Word and PowerPoint open in a faithful view, "Edit" switches to the editor.
            if (app == OfficeConverter.App.Excel && (OfficeConverter.IsOpenXml(_path) || OfficeConverter.CanConvert(app))) { HandOffToEditor(); return; }
            if (OfficeConverter.CanConvert(app)) { BuildOffice(app); return; }
            if (OfficeConverter.IsOpenXml(_path)) { HandOffToEditor(); return; }
        }
        BuildHandlerPreview();
    }

    void HandOffToEditor()
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 240, Height = 4, Margin = new Thickness(0, 0, 0, 14) });
        var label = new TextBlock { Text = L.T("Düzenleyici açılıyor…"), HorizontalAlignment = HorizontalAlignment.Center }; sp.Children.Add(label);
        _body.Content = sp;
        ContentRendered += async (_, _) =>
        {
            if (_officeStarted) return; _officeStarted = true;
            try
            {
                FileViewers.HandingOff.Add(this);
                var w = await FileViewers.OpenEditorAsync(_path, Owner ?? this, t => { label.Text = t; _status.Text = t; });
                if (w != null) { if (w.Owner == this) w.Owner = null; Close(); }
                else FileViewers.HandingOff.Remove(this);
            }
            catch (Exception ex) { FileViewers.HandingOff.Remove(this); ShowFallback(L.T("Düzenleyici açılamadı: ") + ex.Message); }
        };
    }

    /// <summary>The Office document is converted to a PDF view and opened in the PDF studio: zoom, search, text selection, markup and AI.</summary>
    void BuildOffice(OfficeConverter.App app)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 240, Height = 4, Margin = new Thickness(0, 0, 0, 14) });
        sp.Children.Add(new TextBlock { Text = L.F("{0} belgesi görüntülenmek üzere hazırlanıyor…", app), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = L.T("Belge salt okunur açılır, makrolar çalıştırılmaz. İlk açılış birkaç saniye sürebilir."), Foreground = ThemeManager.Brush("TextSecondaryBrush"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        _body.Content = sp;
        _status.Text = L.F("{0} ile hazırlanıyor…", OfficeConverter.EngineName(app));
        var cts = new CancellationTokenSource(); Closed += (_, _) => cts.Cancel();
        ContentRendered += async (_, _) =>
        {
            if (_officeStarted) return; _officeStarted = true;
            try
            {
                var pdf = await OfficeConverter.ToPdfAsync(_path, cts.Token);
                if (!IsVisible) return;
                var viewer = new PdfAttachmentWindow(pdf, FileViewers.DefaultAi(), sourceDocument: _path, sourceKind: KindName(Kind.Preview, _path))
                { Left = Left, Top = Top, Width = Width, Height = Height, WindowStartupLocation = WindowStartupLocation.Manual, Owner = Owner, WindowState = WindowState };
                FileViewers.ReturnFocusOnClose(viewer);
                FileViewers.HandingOff.Add(this);
                viewer.Show(); viewer.Activate(); Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!IsVisible) return;
                _status.Text = L.T("Belge çevrilemedi: ") + ex.Message + L.T(" — Windows önizleyicisi deneniyor.");
                BuildHandlerPreview(started: true);
            }
        };
    }
    bool _officeStarted;

    void BuildHandlerPreview(bool started = false)
    {
        var host = new PreviewHandlerHost();
        if (!host.Open(_path)) { ShowFallback(host.Error ?? L.T("Önizleyici bulunamadı.")); return; }
        _preview = host;
        var loading = new TextBlock { Text = L.T("Belge hazırlanıyor… (Office ilk açılışta birkaç saniye sürebilir)"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("TextSecondaryBrush") };
        var grid = new Grid { Background = ThemeManager.IsDark ? new SolidColorBrush(Color.FromRgb(0x29, 0x29, 0x29)) : Brushes.White };
        grid.Children.Add(loading); grid.Children.Add(host);
        _body.Content = grid;
        _status.Text = L.T("Windows önizleyicisi ile salt okunur gösteriliyor. Düzenlemek için Varsayılan programda açın.");
        void Start()
        {
            if (!host.StartPreview()) { ShowFallback(L.T("Bu belge burada önizlenemedi. Varsayılan programda açabilirsiniz.")); _status.ToolTip = host.Error; return; }
            loading.Visibility = Visibility.Collapsed;
        }
        if (started) Dispatcher.BeginInvoke(new Action(Start), System.Windows.Threading.DispatcherPriority.Background);
        else ContentRendered += (_, _) => Dispatcher.BeginInvoke(new Action(Start), System.Windows.Threading.DispatcherPriority.Background);
    }

    void ShowFallback(string message)
    {
        _preview?.Dispose(); _preview = null;
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
        sp.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 42, HorizontalAlignment = HorizontalAlignment.Center, Foreground = ThemeManager.Brush("TextMutedBrush") });
        sp.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 12) });
        var open = new Button { Content = L.T("Varsayılan programda aç"), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 5, 14, 5) };
        if (TryFindResource("MsPrimaryButtonStyle") is Style ps) open.Style = ps;
        open.Click += (_, _) => Shell(_path); sp.Children.Add(open);
        _body.Content = sp; _status.Text = message;
    }

    // ------------------------------------------------------------------ kod / metin / CNC

    static readonly Dictionary<string, Color> OriginalColors = new();
    static IHighlightingDefinition? _gcode;

    static IHighlightingDefinition GCode()
    {
        if (_gcode != null) return _gcode;
        const string xshd = """
<SyntaxDefinition name="G-Code" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#6A737D" fontStyle="italic"/>
  <Color name="Block" foreground="#8A8A8A"/>
  <Color name="G" foreground="#0F6CBD" fontWeight="bold"/>
  <Color name="M" foreground="#8764B8" fontWeight="bold"/>
  <Color name="Tool" foreground="#CA5010" fontWeight="bold"/>
  <Color name="Feed" foreground="#B8860B"/>
  <Color name="Axis" foreground="#038387"/>
  <Color name="Prog" foreground="#C42B1C" fontWeight="bold"/>
  <Color name="Var" foreground="#107C10"/>
  <Color name="Keyword" foreground="#0F6CBD" fontWeight="bold"/>
  <RuleSet ignoreCase="true">
    <Span color="Comment"><Begin>\(</Begin><End>\)</End></Span>
    <Span color="Comment"><Begin>;</Begin></Span>
    <Rule color="Prog">^\s*%.*$</Rule>
    <Rule color="Prog">\bO\d+</Rule>
    <Rule color="Block">\bN\d+</Rule>
    <Rule color="G">\bG\d+(\.\d+)?</Rule>
    <Rule color="M">\bM\d+</Rule>
    <Rule color="Tool">\b[TDH]\d+</Rule>
    <Rule color="Feed">\b[FS][-+]?(\d+\.?\d*|\.\d+)</Rule>
    <Rule color="Axis">\b[XYZABCUVWIJKRPQ][-+]?(\d+\.?\d*|\.\d+)</Rule>
    <Rule color="Var">[#@]\d+|\bQ\d+</Rule>
    <Keywords color="Keyword">
      <Word>BEGIN</Word><Word>END</Word><Word>PGM</Word><Word>TOOL</Word><Word>CALL</Word><Word>CYCL</Word><Word>DEF</Word><Word>LBL</Word><Word>BLK</Word><Word>FORM</Word>
      <Word>IF</Word><Word>THEN</Word><Word>ELSE</Word><Word>ENDIF</Word><Word>GOTO</Word><Word>WHILE</Word><Word>DO</Word><Word>REPEAT</Word><Word>UNTIL</Word>
    </Keywords>
  </RuleSet>
</SyntaxDefinition>
""";
        using var reader = XmlReader.Create(new StringReader(xshd));
        return _gcode = HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    static Encoding Detect(byte[] bytes, out int bomLength)
    {
        bomLength = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { bomLength = 3; return new UTF8Encoding(true); }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { bomLength = 2; return Encoding.Unicode; }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { bomLength = 2; return Encoding.BigEndianUnicode; }
        try { new UTF8Encoding(false, true).GetString(bytes); return new UTF8Encoding(false); }
        catch { return Encoding.GetEncoding(1254); }
    }

    void BuildCode(string? textOverride = null)
    {
        var ext = Path.GetExtension(_path).ToLowerInvariant();
        string text;
        if (textOverride != null) text = textOverride;
        else
        {
            var bytes = File.ReadAllBytes(_path);
            var truncated = bytes.Length > 30 * 1024 * 1024;
            if (truncated) bytes = bytes[..(30 * 1024 * 1024)];
            _encoding = Detect(bytes, out var bom);
            text = _encoding.GetString(bytes, bom, bytes.Length - bom);
            if (truncated) _status.Text = L.T("Dosya çok büyük; ilk 30 MB gösteriliyor.");
        }
        var ed = new TextEditor
        {
            Text = text, IsReadOnly = true, ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"), FontSize = 13,
            WordWrap = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 2, 2, 2),
        };
        ed.Background = ThemeManager.IsDark ? new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)) : Brushes.White;
        ed.Foreground = ThemeManager.Brush("TextPrimaryBrush");
        ed.LineNumbersForeground = ThemeManager.Brush("TextMutedBrush");
        ed.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x0F, 0x6C, 0xBD));
        ed.TextArea.SelectionForeground = null;
        ed.Options.HighlightCurrentLine = true; ed.Options.EnableHyperlinks = true; ed.Options.ConvertTabsToSpaces = false; ed.Options.ShowTabs = false;
        ed.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(ThemeManager.IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        ed.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        var highlighting = NcExtensions.Contains(ext) ? GCode()
            : ext is ".json" ? HighlightingManager.Instance.GetDefinition("Json") ?? HighlightingManager.Instance.GetDefinition("JavaScript")
            : ext is ".xaml" or ".csproj" or ".props" or ".targets" or ".resx" or ".svg" or ".config" ? HighlightingManager.Instance.GetDefinition("XML")
            : ext is ".ts" or ".tsx" or ".jsx" or ".mjs" ? HighlightingManager.Instance.GetDefinition("JavaScript")
            : ext is ".yml" or ".yaml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".properties" or ".env" ? null
            : HighlightingManager.Instance.GetDefinitionByExtension(ext);
        if (highlighting != null) { AdaptToTheme(highlighting); ed.SyntaxHighlighting = highlighting; }
        SearchPanel.Install(ed);
        ed.PreviewMouseWheel += (_, e) => { if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { ed.FontSize = Math.Clamp(ed.FontSize + (e.Delta > 0 ? 1 : -1), 7, 40); e.Handled = true; UpdateInfo(); } };
        _editor = ed;
        if (ext is ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".resx" or ".svg" or ".config" or ".html" or ".htm")
        { _folding = FoldingManager.Install(ed.TextArea); var s = new XmlFoldingStrategy(); s.UpdateFoldings(_folding, ed.Document); }
        else if (ext is ".cs" or ".java" or ".js" or ".ts" or ".tsx" or ".jsx" or ".c" or ".cpp" or ".cc" or ".hpp" or ".json" or ".css" or ".scss" or ".less" or ".kt" or ".rs" or ".go" or ".php" or ".swift")
        { _folding = FoldingManager.Install(ed.TextArea); BraceFolding(_folding, ed.Document); }
        ed.TextArea.Caret.PositionChanged += (_, _) => UpdateInfo();
        _body.Content = ed;

        _tools.Children.Clear();
        _tools.Children.Add(Toggle("", L.T("Satır kaydır"), L.T("Uzun satırları pencereye sığdır"), false, on => ed.WordWrap = on));
        _tools.Children.Add(Btn("", L.T("Bul (Ctrl+F) / Değiştir (Ctrl+H)"), () => { ed.Focus(); ApplicationCommands.Find.Execute(null, ed.TextArea); }, L.T("Bul")));
        _tools.Children.Add(Btn("", L.T("Yazıyı büyüt (Ctrl+tekerlek)"), () => { ed.FontSize = Math.Min(40, ed.FontSize + 1); UpdateInfo(); }));
        _tools.Children.Add(Btn("", L.T("Yazıyı küçült"), () => { ed.FontSize = Math.Max(7, ed.FontSize - 1); UpdateInfo(); }));
        _tools.Children.Add(Btn("", L.T("Tümünü kopyala"), () => { try { Clipboard.SetText(ed.Text); _status.Text = L.T("Metin panoya kopyalandı."); } catch { } }, L.T("Kopyala")));
        _tools.Children.Add(Toggle("", L.T("Düzenle"), L.T("Düzenlemeyi aç; değişiklikler yalnızca 'Kopyasını kaydet' ile ayrı dosyaya yazılır"), false, on => { ed.IsReadOnly = !on; _status.Text = on ? L.T("Düzenleme açık. Ek dosyasının kendisi değişmez; Kopyasını kaydet ile yazın.") : L.T("Salt okunur."); }));
        if (_kind == Kind.Csv) _tools.Children.Add(Btn("", L.T("Tablo görünümüne dön"), BuildCsv, L.T("Tablo")));
        if (highlighting == null && _status.Text.Length == 0) _status.Text = L.T("Düz metin.");
        else if (_status.Text.Length == 0) _status.Text = (NcExtensions.Contains(ext) ? L.T("CNC programı (G/M kodları, eksenler, takım ve ilerleme renklendirildi).") : L.F("Söz dizimi: {0}.", highlighting!.Name)) + L.T(" Salt okunur.");
        if (NcExtensions.Contains(ext)) _status.Text += "  " + NcSummary(text);
        UpdateInfo();
    }

    static string NcSummary(string text)
    {
        var tools = System.Text.RegularExpressions.Regex.Matches(text, @"\bT(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value.TrimStart('0')).Where(t => t.Length > 0).Distinct().ToList();
        var lines = text.Count(c => c == '\n') + 1;
        return L.F("{0:N0} satır", lines) + (tools.Count > 0 ? L.F(", takımlar: T{0}{1}", string.Join(", T", tools.Take(20)), (tools.Count > 20 ? "…" : "")) : "");
    }

    void UpdateInfo()
    {
        if (_editor == null) return;
        var c = _editor.TextArea.Caret;
        _info.Text = L.F("Satır {0}, Sütun {1}   •   {2:N0} satır   •   {3}   •   {4:0} pt", c.Line, c.Column, _editor.Document.LineCount, _encoding.WebName.ToUpperInvariant(), _editor.FontSize);
    }

    static void AdaptToTheme(IHighlightingDefinition def)
    {
        foreach (var c in def.NamedHighlightingColors)
        {
            var key = def.Name + "/" + c.Name;
            if (c.Foreground is not SimpleHighlightingBrush brush) continue;
            var color = brush.GetColor(null) ?? Colors.Black;
            if (!OriginalColors.ContainsKey(key)) OriginalColors[key] = color;
            var orig = OriginalColors[key];
            var target = orig;
            if (ThemeManager.IsDark)
            {
                var lum = (.299 * orig.R + .587 * orig.G + .114 * orig.B) / 255;
                if (lum < .55) { var t = .55 - lum + .2; target = Color.FromRgb((byte)(orig.R + (255 - orig.R) * t), (byte)(orig.G + (255 - orig.G) * t), (byte)(orig.B + (255 - orig.B) * t)); }
            }
            c.Foreground = new SimpleHighlightingBrush(target);
        }
    }

    static void BraceFolding(FoldingManager manager, ICSharpCode.AvalonEdit.Document.TextDocument doc)
    {
        var list = new List<NewFolding>(); var stack = new Stack<int>(); var text = doc.Text;
        bool inStr = false; char q = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inStr) { if (ch == '\\') i++; else if (ch == q) inStr = false; continue; }
            if (ch is '"' or '\'') { inStr = true; q = ch; continue; }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (ch == '{' || ch == '[') stack.Push(i);
            else if ((ch == '}' || ch == ']') && stack.Count > 0)
            {
                var start = stack.Pop();
                if (doc.GetLineByOffset(start).LineNumber < doc.GetLineByOffset(i).LineNumber) list.Add(new NewFolding(start, i + 1));
            }
        }
        list.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        manager.UpdateFoldings(list, -1);
    }

    // ------------------------------------------------------------------ CSV

    void BuildCsv()
    {
        _folding = null;
        string text;
        try
        {
            var bytes = File.ReadAllBytes(_path); _encoding = Detect(bytes, out var bom); text = _encoding.GetString(bytes, bom, bytes.Length - bom);
        }
        catch (Exception ex) { ShowFallback(L.T("CSV okunamadı: ") + ex.Message); return; }
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var delim = new[] { ';', ',', '\t', '|' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var rows = ParseCsv(text, delim, 50000, out var truncated);
        var table = new DataTable();
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var header = rows.FirstOrDefault() ?? new List<string>();
        for (int i = 0; i < width; i++)
        {
            var name = i < header.Count && header[i].Trim().Length > 0 ? header[i].Trim() : L.F("Sütun {0}", i + 1);
            while (table.Columns.Contains(name)) name += "'";
            table.Columns.Add(name);
        }
        foreach (var r in rows.Skip(1)) { var dr = table.NewRow(); for (int i = 0; i < r.Count && i < width; i++) dr[i] = r[i]; table.Rows.Add(dr); }
        var grid = new DataGrid { ItemsSource = table.DefaultView, IsReadOnly = true, AutoGenerateColumns = true, CanUserSortColumns = true, HeadersVisibility = DataGridHeadersVisibility.All, GridLinesVisibility = DataGridGridLinesVisibility.All,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeaderWidth = 0, Background = ThemeManager.Brush("BgSurfacePrimaryBrush"), RowBackground = ThemeManager.Brush("BgSurfacePrimaryBrush"),
            AlternatingRowBackground = ThemeManager.Brush("BgSurfaceSecondaryBrush"), Foreground = ThemeManager.Brush("TextPrimaryBrush"), HorizontalGridLinesBrush = ThemeManager.Brush("BorderSubtleBrush"), VerticalGridLinesBrush = ThemeManager.Brush("BorderSubtleBrush"), BorderThickness = new Thickness(0) };
        grid.AutoGeneratingColumn += (_, e) => { if (e.Column is DataGridTextColumn c) c.Binding = new System.Windows.Data.Binding($"[{e.PropertyName}]"); e.Column.Header = e.PropertyName; e.Column.MaxWidth = 420; };
        _body.Content = grid;
        _tools.Children.Clear();
        _tools.Children.Add(Btn("", L.T("Ham metin olarak göster"), () => BuildCode(text), L.T("Metin")));
        _tools.Children.Add(Btn("", L.T("Tabloyu sekme ayrılmış olarak kopyala (Excel'e yapıştırılabilir)"), () =>
        {
            var sb = new StringBuilder(); foreach (var r in rows) sb.AppendLine(string.Join('\t', r));
            try { Clipboard.SetText(sb.ToString()); _status.Text = L.T("Tablo panoya kopyalandı; Excel'e yapıştırabilirsiniz."); } catch { }
        }, L.T("Kopyala")));
        var dname = delim switch { ';' => L.T("noktalı virgül"), ',' => L.T("virgül"), '\t' => "sekme", _ => L.T("dikey çizgi") };
        _status.Text = L.F("{0:N0} satır, {1} sütun, ayraç: {2}.", Math.Max(0, rows.Count - 1), width, dname) + (truncated ? L.T(" İlk 50.000 satır gösteriliyor.") : "") + L.T(" Başlığa tıklayarak sıralayabilirsiniz.");
        _info.Text = _encoding.WebName.ToUpperInvariant();
    }

    static List<List<string>> ParseCsv(string text, char d, int max, out bool truncated)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false; truncated = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; } else cell.Append(c); continue; }
            if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == d) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(row); row = new(); if (rows.Count >= max) { truncated = i < text.Length - 1; return rows; } }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); rows.Add(row); }
        return rows;
    }

    // ------------------------------------------------------------------ ZIP

    sealed record ZipRow(string Name, string Folder, string Size, string Packed, string Date, string FullName, long Length);

    void BuildZip()
    {
        List<ZipRow> rows;
        try
        {
            var ext = Path.GetExtension(_path).ToLowerInvariant();
            if (ext == ".zip")
            {
                using var zip = ZipFile.OpenRead(_path);
                rows = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => new ZipRow(e.Name, Path.GetDirectoryName(e.FullName.Replace('/', Path.DirectorySeparatorChar)) ?? "", Size(e.Length), Size(e.CompressedLength), e.LastWriteTime.ToString(L.T("dd.MM.yyyy HH:mm")), e.FullName, e.Length)).ToList();
            }
            else
            {
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(_path, null);
                rows = archive.Entries.Where(e => !e.IsDirectory).Select(e => new ZipRow(Path.GetFileName(e.Key?.Replace('\\', '/') ?? ""), Path.GetDirectoryName((e.Key ?? "").Replace('/', Path.DirectorySeparatorChar)) ?? "", Size(e.Size), Size(e.CompressedSize), (e.LastModifiedTime ?? DateTime.Now).ToString(L.T("dd.MM.yyyy HH:mm")), e.Key ?? "", e.Size)).ToList();
            }
        }
        catch (Exception ex) { ShowFallback(L.T("Arşiv okunamadı: ") + ex.Message); return; }
        var view = new GridView();
        void Col(string header, string path, double w) => view.Columns.Add(new GridViewColumn { Header = header, DisplayMemberBinding = new System.Windows.Data.Binding(path), Width = w });
        Col(L.T("Ad"), nameof(ZipRow.Name), 300); Col(L.T("Klasör"), nameof(ZipRow.Folder), 260); Col(L.T("Boyut"), nameof(ZipRow.Size), 90); Col(L.T("Sıkıştırılmış"), nameof(ZipRow.Packed), 100); Col(L.T("Tarih"), nameof(ZipRow.Date), 130);
        var list = new ListView { View = view, ItemsSource = rows, BorderThickness = new Thickness(0) };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ZipRow r) OpenZipEntry(r); };
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is ZipRow r) OpenZipEntry(r); };
        _body.Content = list;
        _tools.Children.Clear();
        _tools.Children.Add(Btn("", L.T("Seçili dosyayı aç"), () => { if (list.SelectedItem is ZipRow r) OpenZipEntry(r); }, L.T("Aç")));
        _tools.Children.Add(Btn("", L.T("Tümünü bir klasöre çıkar"), ExtractAll, L.T("Tümünü çıkar…")));
        _status.Text = L.F("{0} dosya, toplam {1}. Çift tıklayınca dosya MailStudio görüntüleyicisinde açılır.", rows.Count, Size(rows.Sum(r => r.Length)));
    }

    void OpenZipEntry(ZipRow r)
    {
        try
        {
            ArchiveSecurity.ValidateEntryKey(r.FullName);
            var dir = Path.Combine(Path.GetTempPath(), "MailStudio-zip", Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
            var dest = ArchiveSecurity.GetSafeDestination(dir, Path.GetFileName(r.FullName));
            var ext = Path.GetExtension(_path).ToLowerInvariant();
            if (ext == ".zip")
            {
                using var zip = ZipFile.OpenRead(_path);
                zip.GetEntry(r.FullName)!.ExtractToFile(dest, true);
            }
            else
            {
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(_path, null);
                var entry = archive.Entries.FirstOrDefault(e => e.Key == r.FullName)
                    ?? throw new FileNotFoundException("Entry not found: " + r.FullName);
                using var stream = entry.OpenEntryStream();
                using var fs = File.Create(dest);
                stream.CopyTo(fs);
            }
            FileViewers.Open(dest, this, FileViewers.DefaultAi());
        }
        catch (Exception ex) { _status.Text = L.T("Açılamadı: ") + ex.Message; }
    }

    void ExtractAll()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Arşivin çıkarılacağı klasör") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var target = Path.Combine(dlg.FolderName, Path.GetFileNameWithoutExtension(_path));
            ZipFile.ExtractToDirectory(_path, target, overwriteFiles: false);   // rejects entries that escape the target path
            _status.Text = L.T("Çıkarıldı: ") + target;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _status.Text = L.T("Çıkarılamadı: ") + ex.Message; }
    }

    // ------------------------------------------------------------------ EML

    void BuildEml()
    {
        MimeMessage msg;
        try { msg = MimeMessage.Load(_path); }
        catch (Exception ex) { ShowFallback(L.T(".eml okunamadı: ") + ex.Message); return; }
        var content = MailService.BuildContent(msg);
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var head = new StackPanel { Margin = new Thickness(18, 12, 18, 8) };
        head.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(msg.Subject) ? L.T("(konu yok)") : msg.Subject, FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        head.Children.Add(new TextBlock { Text = msg.From.ToString(), FontSize = 13.5 });
        head.Children.Add(new TextBlock { Text = L.T("Kime: ") + msg.To, Foreground = ThemeManager.Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (msg.Cc.Count > 0) head.Children.Add(new TextBlock { Text = L.T("Bilgi: ") + msg.Cc, Foreground = ThemeManager.Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(new TextBlock { Text = msg.Date.ToLocalTime().ToString(L.T("dd.MM.yyyy dddd HH:mm")), Foreground = ThemeManager.Brush("TextSecondaryBrush") });
        grid.Children.Add(head);
        var atts = new WrapPanel { Margin = new Thickness(18, 0, 18, 6) }; Grid.SetRow(atts, 1); grid.Children.Add(atts);
        foreach (var part in msg.Attachments.OfType<MimePart>())
        {
            var p = part; var name = p.FileName ?? "ek";
            atts.Children.Add(Btn("", L.T("Eki aç"), () =>
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "MailStudio-eml", Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
                    var dest = Path.Combine(dir, string.Join("_", Path.GetFileName(name).Split(Path.GetInvalidFileNameChars())));
                    using (var fs = File.Create(dest)) p.Content!.DecodeTo(fs);
                    FileViewers.Open(dest, this, FileViewers.DefaultAi());
                }
                catch (Exception ex) { _status.Text = L.T("Ek açılamadı: ") + ex.Message; }
            }, name));
        }
        _web = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = ThemeManager.IsDark ? System.Drawing.Color.FromArgb(0x1E, 0x29, 0x3B) : System.Drawing.Color.White };
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Child = _web }; Grid.SetRow(border, 2); grid.Children.Add(border);
        _body.Content = grid;
        _status.Text = L.T("Kaydedilmiş e-posta iletisi. Uzak resimler ve betikler engellendi.");
        Loaded += async (_, _) =>
        {
            try
            {
                await _web.EnsureCoreWebView2Async(await MailHtml.EnvAsync());
                var core = _web.CoreWebView2;
                core.Settings.IsScriptEnabled = false;
                core.Profile.PreferredColorScheme = ThemeManager.IsDark ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;
                core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, a) => { if (a.Request.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) a.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", ""); };
                core.NavigationStarting += (_, a) => { if (a.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; Shell(a.Uri); } };
                core.NewWindowRequested += (_, a) => { a.Handled = true; Shell(a.Uri); };
                MailHtml.Show(_web, content.Html);
            }
            catch (Exception ex) { _status.Text = L.T("İleti gövdesi gösterilemedi: ") + ex.Message; }
        };
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MailStudio.Services.Nc;
using MailStudio.Views.Calendar;
using Microsoft.Win32;

namespace MailStudio.Views.Nc;

/// <summary>
/// Backplot of a CNC program: tool path in top / front / side / isometric view, simulation slider, cycle time estimate,
/// per-tool table and the warnings of the reader. "Compare…" overlays another program (old red, new green) and lists
/// the changed lines with the effect on time and depth.
/// </summary>
public sealed class NcPreviewWindow : Window
{
    readonly NcPathView _view = new();
    readonly ListBox _lines = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, BorderThickness = new Thickness(0) };
    readonly StackPanel _stats = new() { Margin = new Thickness(12, 4, 12, 8) };
    readonly Slider _sim = new() { Minimum = 0, Width = 260, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    readonly TextBlock _simText = PimUi.Text("", 12, brush: "TextSecondaryBrush");
    readonly TextBlock _status = PimUi.Text("", 12, brush: "TextSecondaryBrush");
    NcProgram _program;
    NcComparison? _comparison;
    readonly string _path;

    public NcProgram Program => _program;
    public NcComparison? Comparison => _comparison;

    public NcPreviewWindow(string path)
    {
        _path = path;
        _program = NcProgram.Load(path);
        UiTranslator.SetSkip(this, true);
        Title = L.T("Takım yolu önizlemesi") + " – " + System.IO.Path.GetFileName(path);
        Width = 1280; Height = 820; MinWidth = 900; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 6) };
        T Add<T>(T e) where T : UIElement { bar.Children.Add(e); return e; }
        ToggleButton ViewBtn(string text, NcProjection p)
        {
            var b = new ToggleButton { Content = text, Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 4, 0), IsChecked = p == NcProjection.Iso };
            b.SetResourceReference(StyleProperty, "MsSegmentToggleButtonStyle");
            b.Click += (_, _) => { _view.Projection = p; foreach (var o in bar.Children.OfType<ToggleButton>().Where(x => x.Tag as string == "view")) o.IsChecked = o == b; };
            b.Tag = "view"; return Add(b);
        }
        ViewBtn(L.T("İzometrik"), NcProjection.Iso); ViewBtn(L.T("Üst (XY)"), NcProjection.Top); ViewBtn(L.T("Ön (XZ)"), NcProjection.Front); ViewBtn(L.T("Yan (YZ)"), NcProjection.Side);
        Button Btn(string text, RoutedEventHandler click, string? tip = null)
        {
            var b = new Button { Content = text, Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = tip };
            b.SetResourceReference(StyleProperty, "RibbonCmd"); b.Click += click; return Add(b);
        }
        Btn(L.T("Sığdır"), (_, _) => _view.Fit());
        var rapids = Add(new CheckBox { Content = L.T("Hızlı hareketler"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        rapids.Click += (_, _) => _view.ShowRapids = rapids.IsChecked == true;
        var byTool = Add(new CheckBox { Content = L.T("Takıma göre renk"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        byTool.Click += (_, _) => _view.ColorByTool = byTool.IsChecked == true;
        Add(new TextBlock { Text = L.T("Simülasyon"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
        Add(_sim); _simText.VerticalAlignment = VerticalAlignment.Center; Add(_simText);
        _sim.ValueChanged += (_, _) => OnSim();
        Btn(L.T("Karşılaştır…"), (_, _) => PickCompare(), L.T("Başka bir NC programıyla karşılaştır (eski kırmızı, yeni yeşil)"));
        Btn(L.T("Özeti kopyala"), (_, _) => { try { Clipboard.SetText(_comparison?.Summary() ?? _program.Summary()); _status.Text = L.T("Özet panoya kopyalandı."); } catch { } });

        _view.LineClicked += line => { _lines.SelectedIndex = IndexOfLine(line); _lines.ScrollIntoView(_lines.SelectedItem); };
        _lines.SelectionChanged += (_, _) => { if (_lines.SelectedItem is ListBoxItem { Tag: int l }) _view.SelectedLine = l; };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        var left = new Border { Child = _lines, BorderThickness = new Thickness(1), Margin = new Thickness(10, 0, 0, 0) };
        left.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        grid.Children.Add(left);
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent }; Grid.SetColumn(split, 1); grid.Children.Add(split);
        var viewHost = new Border { Child = _view, BorderThickness = new Thickness(1) };
        viewHost.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        Grid.SetColumn(viewHost, 2); grid.Children.Add(viewHost);
        var right = new ScrollViewer { Content = _stats, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(right, 3); grid.Children.Add(right);

        var foot = new DockPanel { Margin = new Thickness(10, 6, 10, 8) };
        var close = new Button { Content = L.T("Kapat"), MinWidth = 90, Height = 30, IsCancel = true };
        close.SetResourceReference(StyleProperty, "RibbonCmd"); close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); foot.Children.Add(close);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextWrapping = TextWrapping.Wrap; foot.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        root.Children.Add(grid);
        Content = root;
        Display(null);
    }

    int IndexOfLine(int line)
    {
        for (var i = 0; i < _lines.Items.Count; i++) if (_lines.Items[i] is ListBoxItem { Tag: int l } && l == line) return i;
        return -1;
    }

    void OnSim()
    {
        var n = _program.Segments.Count;
        if (n == 0) return;
        var idx = (int)Math.Round(_sim.Value);
        _view.UpTo = idx >= n - 1 ? int.MaxValue : idx;
        var line = _program.Segments[Math.Min(idx, n - 1)].Line;
        _simText.Text = L.F("satır {0}", line + 1);
        if (idx < n - 1) { _lines.SelectedIndex = IndexOfLine(line); if (_lines.SelectedItem != null) _lines.ScrollIntoView(_lines.SelectedItem); }
    }

    /// <summary>Overlays another program (the older one) and lists the differences.</summary>
    public void CompareWith(string otherPath)
    {
        var other = NcProgram.Load(otherPath);
        Display(other);
        _status.Text = _comparison!.Summary();
    }

    void PickCompare()
    {
        var d = new OpenFileDialog { Title = L.T("Karşılaştırılacak (eski) NC programı"), Filter = "CNC|" + string.Join(";", DocumentViewerWindow.NcExtensions.Select(e => "*" + e)) + "|" + L.T("Tüm dosyalar") + "|*.*", InitialDirectory = System.IO.Path.GetDirectoryName(_path) };
        if (d.ShowDialog(this) != true) return;
        try { CompareWith(d.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.ProductName); }
    }

    void Display(NcProgram? other)
    {
        _comparison = other != null ? new NcComparison(other, _program) : null;
        _view.SetPrograms(_program, other);
        _sim.Maximum = Math.Max(0, _program.Segments.Count - 1); _sim.Value = _sim.Maximum; _simText.Text = "";
        _lines.Items.Clear();
        if (_comparison == null)
        {
            for (var i = 0; i < _program.Lines.Count; i++)
                _lines.Items.Add(new ListBoxItem { Tag = i, Content = $"{i + 1,5}  {_program.Lines[i]}", Padding = new Thickness(2, 0, 2, 0) });
        }
        else
        {
            foreach (var (oldLine, newLine, text, kind) in _comparison.Lines)
            {
                var item = new ListBoxItem { Tag = newLine >= 0 ? newLine : -1, Padding = new Thickness(2, 0, 2, 0),
                    Content = $"{(kind == '+' ? "+" : kind == '-' ? "−" : " ")} {(newLine >= 0 ? (newLine + 1).ToString() : ""),5}  {text}" };
                if (kind == '+') item.Background = new SolidColorBrush(Color.FromRgb(223, 246, 221));
                if (kind == '-') { item.Background = new SolidColorBrush(Color.FromRgb(253, 231, 233)); item.Foreground = new SolidColorBrush(Color.FromRgb(160, 30, 40)); }
                _lines.Items.Add(item);
            }
            var first = _comparison.Lines.FindIndex(l => l.Kind != ' ');
            if (first >= 0) { _lines.SelectedIndex = first; _lines.ScrollIntoView(_lines.Items[first]); }
        }
        FillStats();
        _status.Text = _comparison?.Summary() ?? (_program.Warnings.Count > 0 ? L.F("{0} uyarı: okuyucunun izleyemediği komutlar sağ panelde.", _program.Warnings.Count) : L.T("Süre tahmini ivmelenmeyi hesaba katmaz; gerçek süre biraz daha uzun olur."));
    }

    void FillStats()
    {
        _stats.Children.Clear();
        void Head(string t) { var h = PimUi.Text(t, 13, bold: true); h.Margin = new Thickness(0, 10, 0, 4); _stats.Children.Add(h); }
        void Row(string label, string value)
        {
            var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var v = PimUi.Text(value, 12.5, bold: true); DockPanel.SetDock(v, Dock.Right); d.Children.Add(v);
            d.Children.Add(PimUi.Text(label, 12.5, brush: "TextSecondaryBrush")); _stats.Children.Add(d);
        }
        var p = _program;
        var big = PimUi.Text(NcProgram.FormatMinutes(p.TotalMinutes), 26, bold: true, brush: "AccentPrimaryBrush");
        _stats.Children.Add(PimUi.Text(L.T("Tahmini çevrim süresi"), 12, brush: "TextSecondaryBrush")); _stats.Children.Add(big);
        if (_comparison != null)
        {
            var delta = p.TotalMinutes - _comparison.Old.TotalMinutes;
            _stats.Children.Add(PimUi.Text(L.F("önceki: {0}  ({1}{2:0.0} dk)", NcProgram.FormatMinutes(_comparison.Old.TotalMinutes), delta >= 0 ? "+" : "", delta), 12, brush: delta > 0 ? "StatusDangerBrush" : "TextSecondaryBrush"));
            Row(L.T("Değişen satır"), $"+{_comparison.Added} / −{_comparison.Removed}");
        }
        Head(L.T("Süre dağılımı"));
        Row(L.T("Kesme"), NcProgram.FormatMinutes(p.FeedMinutes));
        Row(L.T("Hızlı hareket"), NcProgram.FormatMinutes(p.RapidMinutes));
        Row(L.T("Takım değişimi"), NcProgram.FormatMinutes(p.ToolChangeMinutes));
        if (p.DwellMinutes > 0) Row(L.T("Bekleme"), NcProgram.FormatMinutes(p.DwellMinutes));
        Head(L.T("Yol"));
        Row(L.T("Kesme yolu"), $"{p.FeedLength:0} mm");
        Row(L.T("Hızlı yol"), $"{p.RapidLength:0} mm");
        Head(L.T("Sınırlar (mm)"));
        Row("X", $"{p.MinX:0.###} … {p.MaxX:0.###}");
        Row("Y", $"{p.MinY:0.###} … {p.MaxY:0.###}");
        Row("Z", $"{p.MinZ:0.###} … {p.MaxZ:0.###}");
        if (p.Inch) _stats.Children.Add(PimUi.Text(L.T("Program inç (G20); değerler mm'ye çevrildi."), 11.5, brush: "TextMutedBrush"));
        Head(L.F("Takımlar ({0})", p.Tools.Count));
        foreach (var t in p.Tools)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new Border { Width = 10, Height = 10, Background = new SolidColorBrush(NcPathView.ColorOfTool(t.Tool)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var txt = PimUi.Text($"T{t.Tool}  {NcProgram.FormatMinutes(t.Minutes)}  ·  Z {t.MinZ:0.###}" + (t.Holes > 0 ? "  ·  " + L.F("{0} delik", t.Holes) : ""), 12.5, bold: true);
            sp.Children.Add(txt); _stats.Children.Add(sp);
            if (t.Comment.Length > 0) { var c = PimUi.Text(t.Comment, 11.5, brush: "TextSecondaryBrush"); c.Margin = new Thickness(16, 0, 0, 2); c.TextWrapping = TextWrapping.Wrap; _stats.Children.Add(c); }
        }
        if (p.Warnings.Count > 0)
        {
            Head(L.F("Uyarılar ({0})", p.Warnings.Count));
            foreach (var w in p.Warnings.Take(30)) { var t = PimUi.Text("• " + w, 11.5, brush: "StatusDangerBrush"); t.TextWrapping = TextWrapping.Wrap; _stats.Children.Add(t); }
        }
    }
}

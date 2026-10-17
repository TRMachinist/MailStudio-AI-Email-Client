using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>Rule manager like Outlook "Conditional Formatting". Rules are created manually or in natural language (AI);
/// the AI output is never saved directly, it fills the editor and the user confirms.</summary>
public sealed class FormatRulesWindow : Window
{
    static readonly (string Name, string? Hex)[] Palette =
    {
        (L.T("Yok"), null), (L.T("Kırmızı"), "#C42B1C"), (L.T("Turuncu"), "#CA5010"), (L.T("Sarı"), "#B8860B"), (L.T("Yeşil"), "#107C10"),
        (L.T("Turkuaz"), "#038387"), (L.T("Mavi"), "#0F6CBD"), (L.T("Mor"), "#8764B8"), (L.T("Gri"), "#616161"),
    };
    static readonly (string Name, string? Hex)[] BackPalette =
    {
        (L.T("Yok"), null), (L.T("Açık kırmızı"), "#FDE7E9"), (L.T("Açık turuncu"), "#FDEBDD"), (L.T("Açık sarı"), "#FFF4CE"), (L.T("Açık yeşil"), "#DFF6DD"),
        (L.T("Açık mavi"), "#DEECF9"), (L.T("Açık mor"), "#EFE6FB"), (L.T("Açık gri"), "#EDEBE9"),
    };

    readonly List<FormatRule> _rules;
    readonly IReadOnlyList<MailHeader> _sample;
    readonly Func<string, CancellationToken, Task<string>> _ai;
    readonly Func<string> _providerName;
    readonly ListBox _list = new() { MinHeight = 120 };
    readonly TextBox _name = new();
    readonly CheckBox _enabled = new() { Content = L.T("Etkin") };
    readonly StackPanel _conds = new();
    readonly ComboBox _fore = new(), _back = new();
    readonly CheckBox _bold = new() { Content = L.T("Kalın"), Margin = new Thickness(0, 0, 12, 0) }, _italic = new() { Content = L.T("İtalik") };
    readonly TextBlock _preview = new() { Text = L.T("Örnek: Teklif — 120 adet flanş"), Padding = new Thickness(8, 6, 8, 6) };
    readonly Border _previewBox = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock _matchInfo = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox _aiBox = new() { MinHeight = 56, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    readonly Button _aiButton = new() { Content = L.T("Yapay zekayla kural oluştur"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _aiStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    FormatRule? _edit;
    bool _loading;
    CancellationTokenSource? _aiCts;

    public List<FormatRule> Rules => _rules;

    public FormatRulesWindow(IEnumerable<FormatRule> rules, IReadOnlyList<MailHeader> sample,
        Func<string, CancellationToken, Task<string>> ai, Func<string> providerName)
    {
        _rules = rules.Select(r => r.Clone()).ToList();
        _sample = sample; _ai = ai; _providerName = providerName;
        Title = L.T("Koşullu Biçimlendirme"); Width = 900; Height = 640; MinWidth = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        Closed += (_, _) => _aiCts?.Cancel();

        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;

        var head = new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 0, 0, 1), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), BorderBrush = ThemeManager.Brush("BorderSubtleBrush") };
        var hs = new StackPanel(); head.Child = hs; root.Children.Add(head);
        hs.Children.Add(new TextBlock { Text = L.T("Koşullu Biçimlendirme"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        hs.Children.Add(new TextBlock { Text = L.T("Koşulları sağlayan iletiler listede renk, zemin ve yazı biçimiyle öne çıkar. Kurallar yukarıdan aşağı denenir; ilk eşleşen uygulanır. Sunucudaki iletilere dokunulmaz."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = ThemeManager.Brush("TextSecondaryBrush") });

        var body = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(280) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1); root.Children.Add(body);

        // Sol: kural listesi + yapay zeka
        var left = new DockPanel();
        body.Children.Add(left);
        var listBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(listBar, Dock.Bottom);
        var aiPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(aiPanel, Dock.Bottom);
        left.Children.Add(aiPanel); left.Children.Add(listBar);
        var listHead = new TextBlock { Text = L.T("Kurallar"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(listHead, Dock.Top); left.Children.Add(listHead);
        left.Children.Add(_list);
        listBar.Children.Add(Btn(L.T("Yeni"), (_, _) => AddRule(new FormatRule { Conditions = { new FormatCondition { Field = "from" } }, TextColor = "#0F6CBD" })));
        listBar.Children.Add(Btn(L.T("Sil"), (_, _) => { if (_edit != null) { _rules.Remove(_edit); _edit = null; RefreshList(0); } }));
        listBar.Children.Add(Btn(L.T("Yukarı"), (_, _) => Move(-1)));
        listBar.Children.Add(Btn(L.T("Aşağı"), (_, _) => Move(1)));
        _list.SelectionChanged += (_, _) => Load(_list.SelectedIndex >= 0 && _list.SelectedIndex < _rules.Count ? _rules[_list.SelectedIndex] : null);

        aiPanel.Children.Add(new TextBlock { Text = L.T("Doğal dille anlat"), FontWeight = FontWeights.SemiBold });
        aiPanel.Children.Add(new TextBlock { Text = L.T("Örn: \"Ahmet'ten gelen okunmamış teklifleri kalın kırmızı yap\""), TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("TextSecondaryBrush"), Margin = new Thickness(0, 2, 0, 6) });
        aiPanel.Children.Add(_aiBox); aiPanel.Children.Add(_aiButton); aiPanel.Children.Add(_aiStatus);
        _aiButton.Click += async (_, _) => await AiCreateAsync();
        _aiBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await AiCreateAsync(); } };

        // Right: editor
        var editorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(editorScroll, 2); body.Children.Add(editorScroll);
        var ed = new StackPanel(); editorScroll.Content = ed;
        ed.Children.Add(Label(L.T("Kural adı")));
        var nameRow = new DockPanel(); DockPanel.SetDock(_enabled, Dock.Right); _enabled.Margin = new Thickness(12, 0, 0, 0); _enabled.VerticalAlignment = VerticalAlignment.Center;
        nameRow.Children.Add(_enabled); nameRow.Children.Add(_name); ed.Children.Add(nameRow);
        ed.Children.Add(Label(L.T("Koşullar (hepsi sağlanmalı; değerde ; ile birden fazla seçenek yazılabilir)")));
        ed.Children.Add(_conds);
        ed.Children.Add(Btn(L.T("+ Koşul ekle"), (_, _) => { if (_edit == null) return; Store(); _edit.Conditions.Add(new FormatCondition { Field = "subject" }); Load(_edit); }, HorizontalAlignment.Left));
        ed.Children.Add(Label(L.T("Biçim")));
        var fmt = new WrapPanel();
        fmt.Children.Add(new TextBlock { Text = L.T("Yazı rengi"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        Fill(_fore, Palette); _fore.Width = 150; _fore.Margin = new Thickness(0, 0, 16, 0); fmt.Children.Add(_fore);
        fmt.Children.Add(new TextBlock { Text = L.T("Zemin"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        Fill(_back, BackPalette); _back.Width = 150; _back.Margin = new Thickness(0, 0, 16, 0); fmt.Children.Add(_back);
        _bold.VerticalAlignment = _italic.VerticalAlignment = VerticalAlignment.Center;
        fmt.Children.Add(_bold); fmt.Children.Add(_italic);
        ed.Children.Add(fmt);
        _previewBox.Child = _preview; ed.Children.Add(_previewBox);
        _matchInfo.Foreground = ThemeManager.Brush("TextSecondaryBrush"); ed.Children.Add(_matchInfo);
        _name.TextChanged += (_, _) => Changed();
        foreach (var c in new[] { _enabled, _bold, _italic }) { c.Checked += (_, _) => Changed(); c.Unchecked += (_, _) => Changed(); }
        _fore.SelectionChanged += (_, _) => Changed(); _back.SelectionChanged += (_, _) => Changed();

        // Alt bar
        var footer = new Border { Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 1, 0, 0), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), BorderBrush = ThemeManager.Brush("BorderSubtleBrush") };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        var fb = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Child = fb;
        var ok = new Button { Content = L.T("Kaydet ve uygula"), IsDefault = true, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        if (TryFindResource("MsPrimaryButtonStyle") is Style ps) ok.Style = ps;
        ok.Click += (_, _) => { Store(); _rules.RemoveAll(r => r.Conditions.Count == 0); DialogResult = true; };
        var cancel = new Button { Content = L.T("Vazgeç"), IsCancel = true, MinWidth = 90 };
        fb.Children.Add(ok); fb.Children.Add(cancel);

        RefreshList(0);
    }

    static TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
    static Button Btn(string t, RoutedEventHandler h, HorizontalAlignment a = HorizontalAlignment.Stretch)
    { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = a }; b.Click += h; return b; }

    public sealed record Swatch(string Name, string? Hex)
    {
        public Brush Fill => Hex == null ? Brushes.Transparent : Hex_(Hex);
        static Brush Hex_(string h) { try { var b = (Brush)new BrushConverter().ConvertFromString(h)!; b.Freeze(); return b; } catch { return Brushes.Transparent; } }
    }

    static DataTemplate SwatchTemplate()
    {
        var sp = new FrameworkElementFactory(typeof(StackPanel)); sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(WidthProperty, 14.0); bd.SetValue(HeightProperty, 14.0); bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(2)); bd.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1)); bd.SetValue(Border.BorderBrushProperty, ThemeManager.Brush("BorderStrongBrush"));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Swatch.Fill)));
        var tb = new FrameworkElementFactory(typeof(TextBlock)); tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Swatch.Name))); tb.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        sp.AppendChild(bd); sp.AppendChild(tb);
        return new DataTemplate { VisualTree = sp };
    }

    static void Fill(ComboBox box, (string Name, string? Hex)[] items)
    {
        box.ItemTemplate = SwatchTemplate();
        foreach (var (name, hex) in items) box.Items.Add(new Swatch(name, hex));
        box.SelectedIndex = 0;
    }

    static Brush Hex(string hex) { try { return (Brush)new BrushConverter().ConvertFromString(hex)!; } catch { return Brushes.Transparent; } }

    static void Select(ComboBox box, string? hex)
    {
        for (int i = 0; i < box.Items.Count; i++)
            if (string.Equals(((Swatch)box.Items[i]).Hex, hex, StringComparison.OrdinalIgnoreCase)) { box.SelectedIndex = i; return; }
        if (hex != null) { box.Items.Add(new Swatch(hex, hex)); box.SelectedIndex = box.Items.Count - 1; return; }   // if the AI returned a color outside the palette
        box.SelectedIndex = 0;
    }

    void AddRule(FormatRule r) { Store(); _rules.Insert(0, r); RefreshList(0); }

    void Move(int d)
    {
        if (_edit == null) return; Store();
        var i = _rules.IndexOf(_edit); var j = i + d;
        if (j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]); RefreshList(j);
    }

    void RefreshList(int select)
    {
        _list.Items.Clear();
        foreach (var r in _rules)
        {
            var sp = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            var t = new TextBlock { Text = r.Name, FontWeight = r.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = r.Italic ? FontStyles.Italic : FontStyles.Normal };
            if (r.TextColor != null) t.Foreground = Hex(r.TextColor);
            if (!r.Enabled) t.Opacity = .5;
            sp.Children.Add(t);
            sp.Children.Add(new TextBlock { Text = string.Join(" ve ", r.Conditions.Select(FormatRuleEngine.Describe)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeManager.Brush("TextSecondaryBrush") });
            _list.Items.Add(new ListBoxItem { Content = sp, Background = r.BackColor != null ? Hex(r.BackColor) : Brushes.Transparent });
        }
        if (_rules.Count == 0) { Load(null); return; }
        _list.SelectedIndex = Math.Clamp(select, 0, _rules.Count - 1);
    }

    void Load(FormatRule? r)
    {
        if (_edit != null && !ReferenceEquals(r, _edit)) Store();
        _loading = true; _edit = r;
        _conds.Children.Clear();
        IsEditorEnabled(r != null);
        if (r != null)
        {
            _name.Text = r.Name; _enabled.IsChecked = r.Enabled; _bold.IsChecked = r.Bold; _italic.IsChecked = r.Italic;
            Select(_fore, r.TextColor); Select(_back, r.BackColor);
            foreach (var c in r.Conditions) _conds.Children.Add(ConditionRow(c));
        }
        _loading = false; UpdatePreview();
    }

    void IsEditorEnabled(bool on) { foreach (UIElement e in new UIElement[] { _name, _enabled, _fore, _back, _bold, _italic, _conds }) e.IsEnabled = on; }

    UIElement ConditionRow(FormatCondition c)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var field = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var f in FormatRuleEngine.Fields) field.Items.Add(new ComboBoxItem { Content = f.Label, Tag = f.Field });
        field.SelectedIndex = Math.Max(0, Array.FindIndex(FormatRuleEngine.Fields, f => f.Field == c.Field));
        var value = new TextBox { Text = c.Value };
        var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Width = 28, Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("Koşulu kaldır") };
        DockPanel.SetDock(field, Dock.Left); DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(field); row.Children.Add(remove); row.Children.Add(value);
        void Sync()
        {
            var f = FormatRuleEngine.Fields[Math.Max(0, field.SelectedIndex)];
            value.Visibility = f.NeedsValue ? Visibility.Visible : Visibility.Hidden;
            c.Field = f.Field; c.Value = f.NeedsValue ? value.Text : "";
            Changed();
        }
        field.SelectionChanged += (_, _) => Sync();
        value.TextChanged += (_, _) => Sync();
        remove.Click += (_, _) => { if (_edit == null) return; Store(); _edit.Conditions.Remove(c); Load(_edit); };
        value.Visibility = FormatRuleEngine.Fields[Math.Max(0, field.SelectedIndex)].NeedsValue ? Visibility.Visible : Visibility.Hidden;
        return row;
    }

    void Store()
    {
        if (_edit == null) return;
        _edit.Name = string.IsNullOrWhiteSpace(_name.Text) ? L.T("Adsız kural") : _name.Text.Trim();
        _edit.Enabled = _enabled.IsChecked == true; _edit.Bold = _bold.IsChecked == true; _edit.Italic = _italic.IsChecked == true;
        _edit.TextColor = (_fore.SelectedItem as Swatch)?.Hex;
        _edit.BackColor = (_back.SelectedItem as Swatch)?.Hex;
    }

    void Changed()
    {
        if (_loading || _edit == null) return;
        Store(); UpdatePreview();
        var i = _rules.IndexOf(_edit);
        if (i >= 0 && i < _list.Items.Count && _list.Items[i] is ListBoxItem li && li.Content is StackPanel sp && sp.Children.Count == 2)
        {
            var t = (TextBlock)sp.Children[0]; t.Text = _edit.Name; t.FontWeight = _edit.Bold ? FontWeights.Bold : FontWeights.Normal; t.FontStyle = _edit.Italic ? FontStyles.Italic : FontStyles.Normal;
            t.Foreground = _edit.TextColor != null ? Hex(_edit.TextColor) : ThemeManager.Brush("TextPrimaryBrush"); t.Opacity = _edit.Enabled ? 1 : .5;
            ((TextBlock)sp.Children[1]).Text = string.Join(" ve ", _edit.Conditions.Select(FormatRuleEngine.Describe));
            li.Background = _edit.BackColor != null ? Hex(_edit.BackColor) : Brushes.Transparent;
        }
    }

    void UpdatePreview()
    {
        var r = _edit;
        _previewBox.BorderBrush = ThemeManager.Brush("BorderSubtleBrush");
        _previewBox.Background = r?.BackColor != null ? Hex(r.BackColor) : ThemeManager.Brush("BgSurfaceSecondaryBrush");
        _preview.Foreground = r?.TextColor != null ? Hex(r.TextColor) : ThemeManager.Brush("TextPrimaryBrush");
        _preview.FontWeight = r?.Bold == true ? FontWeights.Bold : FontWeights.Normal;
        _preview.FontStyle = r?.Italic == true ? FontStyles.Italic : FontStyles.Normal;
        if (r == null) { _matchInfo.Text = L.T("Kural seçin veya Yeni ile oluşturun."); return; }
        var probe = r.Clone(); probe.Enabled = true;
        var hits = _sample.Where(h => FormatRuleEngine.Matches(probe, h)).ToList();
        _matchInfo.Text = _sample.Count == 0 ? L.T("Açık klasörde ileti yok; eşleşme sayılamadı.")
            : L.F("Açık klasördeki {0} iletiden {1} tanesi eşleşiyor", _sample.Count, hits.Count) + (hits.Count > 0 ? ": " + string.Join(", ", hits.Take(3).Select(h => h.Subject)) + (hits.Count > 3 ? "…" : "") : ".");
    }

    async Task AiCreateAsync()
    {
        var text = _aiBox.Text.Trim();
        if (text.Length == 0) { _aiStatus.Text = L.T("Önce kuralı birkaç kelimeyle anlatın."); return; }
        _aiCts?.Cancel(); _aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        _aiButton.IsEnabled = false; _aiStatus.Foreground = ThemeManager.Brush("TextSecondaryBrush");
        _aiStatus.Text = L.F("{0} kural hazırlıyor… Pencere kullanılmaya devam edilebilir.", _providerName());
        try
        {
            var reply = await _ai(FormatRuleEngine.AiPrompt(text), _aiCts.Token);
            var rule = FormatRuleEngine.ParseAi(reply);
            AddRule(rule);
            _aiStatus.Text = L.T("Kural düzenleyiciye eklendi. Koşulları kontrol edip Kaydet ve uygula'ya basın.");
        }
        catch (OperationCanceledException) { _aiStatus.Text = L.T("İstek iptal edildi veya zaman aşımına uğradı."); }
        catch (Exception ex) { _aiStatus.Foreground = ThemeManager.Brush("StatusDangerBrush"); _aiStatus.Text = L.T("Kural oluşturulamadı: ") + ex.Message; }
        finally { _aiButton.IsEnabled = true; }
    }
}

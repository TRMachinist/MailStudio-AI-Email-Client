using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// Outlook-style Rules Manager: manages automated incoming mail rules,
/// supports rule ordering, stop-processing, multi-conditions, actions, AI natural-language creation, and test preview.
/// </summary>
public sealed class RulesWindow : Window
{
    readonly RuleStore _store;
    readonly List<MailRule> _rules;
    readonly IReadOnlyList<MailHeader> _sample;
    readonly Func<string, CancellationToken, Task<string>> _ai;
    readonly Func<string> _providerName;
    readonly Func<MailRule, Task>? _runOnFolder;

    readonly ListBox _list = new() { MinHeight = 140 };
    readonly TextBox _name = new();
    readonly CheckBox _enabled = new() { Content = L.T("Etkin") };
    readonly CheckBox _stopProcessing = new() { Content = L.T("Sonraki kuralları işleme") };
    readonly ComboBox _matchMode = new();
    readonly StackPanel _condsPanel = new();
    readonly StackPanel _actionsPanel = new();
    readonly TextBlock _matchInfo = new() { Margin = new Thickness(0, 6, 0, 0), Foreground = ThemeManager.Brush("TextSecondaryBrush") };
    readonly TextBox _aiBox = new() { MinHeight = 56, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    readonly Button _aiButton = new() { Content = L.T("Yapay zekayla kural oluştur"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _aiStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    MailRule? _edit;
    bool _loading;
    CancellationTokenSource? _aiCts;

    public List<MailRule> Rules => _rules;

    public RulesWindow(RuleStore store, IReadOnlyList<MailHeader> sample,
        Func<string, CancellationToken, Task<string>> ai, Func<string> providerName,
        MailRule? newRuleToEdit = null, Func<MailRule, Task>? runOnFolder = null)
    {
        _store = store;
        _rules = store.Rules.Select(r => r.Clone()).ToList();
        _sample = sample;
        _ai = ai;
        _providerName = providerName;
        _runOnFolder = runOnFolder;
        UiTranslator.SetSkip(_list, true);   // rule names are user text

        Title = L.T("Kurallar ve Uyarılar");
        Width = 920;
        Height = 650;
        MinWidth = 740;
        MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        Closed += (_, _) => _aiCts?.Cancel();

        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;

        // Üst başlık
        var head = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"),
            BorderBrush = ThemeManager.Brush("BorderSubtleBrush")
        };
        var hs = new StackPanel();
        head.Child = hs;
        root.Children.Add(head);
        hs.Children.Add(new TextBlock { Text = L.T("E-posta Kuralları"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        hs.Children.Add(new TextBlock
        {
            Text = L.T("Gelen iletiler için otomatik kurallar. Kurallar yukarıdan aşağı denenir; eşleşen kuraldaki eylemler uygulanır."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = ThemeManager.Brush("TextSecondaryBrush")
        });

        // Gövde (Sol: liste + AI, Sağ: düzenleyici)
        var body = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(290) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // Sol taraf
        var left = new DockPanel();
        body.Children.Add(left);

        var listBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(listBar, Dock.Bottom);

        var aiPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(aiPanel, Dock.Bottom);
        left.Children.Add(aiPanel);
        left.Children.Add(listBar);

        var listHead = new TextBlock { Text = L.T("Kurallar"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(listHead, Dock.Top);
        left.Children.Add(listHead);
        left.Children.Add(_list);

        listBar.Children.Add(Btn(L.T("Yeni"), (_, _) => AddRule(new MailRule
        {
            Name = L.T("Yeni kural"),
            Conditions = { new RuleCondition { Field = "from", Value = "" } },
            Actions = { new RuleAction { Type = "move_to_folder", Value = "Teklifler" } }
        })));
        listBar.Children.Add(Btn(L.T("Sil"), (_, _) =>
        {
            if (_edit != null)
            {
                _rules.Remove(_edit);
                _edit = null;
                RefreshList(0);
            }
        }));
        listBar.Children.Add(Btn(L.T("Yukarı"), (_, _) => Move(-1)));
        listBar.Children.Add(Btn(L.T("Aşağı"), (_, _) => Move(1)));

        _list.SelectionChanged += (_, _) => Load(_list.SelectedIndex >= 0 && _list.SelectedIndex < _rules.Count ? _rules[_list.SelectedIndex] : null);

        aiPanel.Children.Add(new TextBlock { Text = L.T("Doğal dille kural oluştur"), FontWeight = FontWeights.SemiBold });
        aiPanel.Children.Add(new TextBlock
        {
            Text = L.T("Örn: \"Anna'dan gelen teklifleri Teklifler klasörüne taşı ve bayrakla\""),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeManager.Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 2, 0, 6)
        });
        aiPanel.Children.Add(_aiBox);
        aiPanel.Children.Add(_aiButton);
        aiPanel.Children.Add(_aiStatus);

        _aiButton.Click += async (_, _) => await AiCreateAsync();
        _aiBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await AiCreateAsync();
            }
        };

        // Sağ taraf: Kural düzenleyici
        var editorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(editorScroll, 2);
        body.Children.Add(editorScroll);

        var ed = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        editorScroll.Content = ed;

        ed.Children.Add(Label(L.T("Kural adı")));
        var nameRow = new DockPanel();
        DockPanel.SetDock(_enabled, Dock.Right);
        _enabled.Margin = new Thickness(12, 0, 0, 0);
        _enabled.VerticalAlignment = VerticalAlignment.Center;
        nameRow.Children.Add(_enabled);
        nameRow.Children.Add(_name);
        ed.Children.Add(nameRow);

        var optRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        _stopProcessing.Margin = new Thickness(0, 0, 16, 0);
        _stopProcessing.VerticalAlignment = VerticalAlignment.Center;
        optRow.Children.Add(_stopProcessing);

        _matchMode.Items.Add(L.T("Tüm koşullar sağlandığında (VE)"));
        _matchMode.Items.Add(L.T("Herhangi bir koşul sağlandığında (VEYA)"));
        _matchMode.SelectedIndex = 0;
        _matchMode.VerticalAlignment = VerticalAlignment.Center;
        optRow.Children.Add(_matchMode);
        ed.Children.Add(optRow);

        ed.Children.Add(Label(L.T("Koşullar")));
        ed.Children.Add(_condsPanel);
        ed.Children.Add(Btn(L.T("+ Koşul ekle"), (_, _) =>
        {
            if (_edit == null) return;
            Store();
            _edit.Conditions.Add(new RuleCondition { Field = "subject", Value = "" });
            Load(_edit);
        }, HorizontalAlignment.Left));

        ed.Children.Add(Label(L.T("Eylemler")));
        ed.Children.Add(_actionsPanel);
        ed.Children.Add(Btn(L.T("+ Eylem ekle"), (_, _) =>
        {
            if (_edit == null) return;
            Store();
            _edit.Actions.Add(new RuleAction { Type = "move_to_folder", Value = "" });
            Load(_edit);
        }, HorizontalAlignment.Left));

        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var testBtn = Btn(L.T("Bu kuralı test et"), (_, _) => TestCurrentRule(), HorizontalAlignment.Left);
        testRow.Children.Add(testBtn);
        ed.Children.Add(testRow);
        ed.Children.Add(_matchInfo);

        _name.TextChanged += (_, _) => Changed();
        _enabled.Checked += (_, _) => Changed();
        _enabled.Unchecked += (_, _) => Changed();
        _stopProcessing.Checked += (_, _) => Changed();
        _stopProcessing.Unchecked += (_, _) => Changed();
        _matchMode.SelectionChanged += (_, _) => Changed();

        // Alt bar
        var footer = new Border
        {
            Padding = new Thickness(16, 10, 16, 10),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"),
            BorderBrush = ThemeManager.Brush("BorderSubtleBrush")
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        var footGrid = new DockPanel();
        footer.Child = footGrid;

        if (_runOnFolder != null)
        {
            var runBtn = new Button { Content = L.T("Klasörde şimdi çalıştır"), Padding = new Thickness(10, 4, 10, 4) };
            runBtn.Click += async (_, _) =>
            {
                if (_edit == null) return;
                Store();
                RuleEngine.Normalize(_edit);
                try { await _runOnFolder(_edit); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Kurallar")); }
            };
            DockPanel.SetDock(runBtn, Dock.Left);
            footGrid.Children.Add(runBtn);
        }

        var fb = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footGrid.Children.Add(fb);

        var ok = new Button { Content = L.T("Kaydet ve Kapat"), IsDefault = true, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        if (TryFindResource("MsPrimaryButtonStyle") is Style ps) ok.Style = ps;
        ok.Click += (_, _) =>
        {
            Store();
            _rules.RemoveAll(r => r.Conditions.Count == 0 && r.Actions.Count == 0);
            foreach (var r in _rules)
            {
                RuleEngine.Normalize(r);
                if (RuleEngine.Validate(r) is { } problem)
                {
                    RefreshList(_rules.IndexOf(r));
                    MessageBox.Show(this, $"{r.Name}: {problem}", L.T("Kurallar"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            try
            {
                _store.Rules.Clear();
                _store.Rules.AddRange(_rules);
                _store.Save();
            }
            catch (Exception ex) { MessageBox.Show(this, L.T("Kurallar kaydedilemedi: ") + ex.Message, L.T("Kurallar")); return; }
            Done(true);
        };

        var cancel = new Button { Content = L.T("Kapat"), IsCancel = true, MinWidth = 90 };
        cancel.Click += (_, _) => Done(false);

        fb.Children.Add(ok);
        fb.Children.Add(cancel);

        if (newRuleToEdit != null)
        {
            _rules.Insert(0, newRuleToEdit);
            RefreshList(0);
        }
        else
        {
            RefreshList(0);
        }
    }

    static TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    static Button Btn(string t, RoutedEventHandler h, HorizontalAlignment a = HorizontalAlignment.Stretch)
    {
        var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = a };
        b.Click += h;
        return b;
    }

    void AddRule(MailRule r)
    {
        Store();
        _rules.Insert(0, r);
        RefreshList(0);
    }

    void Move(int d)
    {
        if (_edit == null) return;
        Store();
        var i = _rules.IndexOf(_edit);
        var j = i + d;
        if (j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
        RefreshList(j);
    }

    void Done(bool ok)
    {
        try { DialogResult = ok; } catch (InvalidOperationException) { }   // opened with Show()
        Close();
    }

    void RefreshList(int select)
    {
        _list.Items.Clear();
        for (int i = 0; i < _rules.Count; i++)
        {
            var r = _rules[i];
            var sp = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            var cb = new CheckBox { Content = r.Name, IsChecked = r.Enabled, FontWeight = FontWeights.SemiBold };
            var ruleRef = r;
            cb.Click += (_, _) => { ruleRef.Enabled = cb.IsChecked == true; };
            if (!r.Enabled) cb.Opacity = 0.55;
            sp.Children.Add(cb);

            var desc = string.Join(" " + (r.MatchAll ? L.T("ve ayrıca") : L.T("ya da")) + " ", r.Conditions.Select(RuleEngine.DescribeCondition));
            if (string.IsNullOrEmpty(desc)) desc = L.T("(Koşul yok)");
            var actionsDesc = string.Join(", ", r.Actions.Select(RuleEngine.DescribeAction));
            if (string.IsNullOrEmpty(actionsDesc)) actionsDesc = L.T("(Eylem yok)");

            sp.Children.Add(new TextBlock
            {
                Text = $"{desc} ➔ {actionsDesc}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeManager.Brush("TextSecondaryBrush"),
                Margin = new Thickness(18, 2, 0, 0)
            });
            _list.Items.Add(new ListBoxItem { Content = sp });
        }

        if (_rules.Count == 0) { Load(null); return; }
        _list.SelectedIndex = Math.Clamp(select, 0, _rules.Count - 1);
    }

    void Load(MailRule? r)
    {
        if (_edit != null && !ReferenceEquals(r, _edit)) Store();
        _loading = true;
        _edit = r;
        _condsPanel.Children.Clear();
        _actionsPanel.Children.Clear();
        IsEditorEnabled(r != null);
        if (r != null)
        {
            _name.Text = r.Name;
            _enabled.IsChecked = r.Enabled;
            _stopProcessing.IsChecked = r.StopProcessing;
            _matchMode.SelectedIndex = r.MatchAll ? 0 : 1;

            foreach (var c in r.Conditions) _condsPanel.Children.Add(ConditionRow(c));
            foreach (var a in r.Actions) _actionsPanel.Children.Add(ActionRow(a));
        }
        _loading = false;
        TestCurrentRule();
    }

    void IsEditorEnabled(bool on)
    {
        foreach (UIElement e in new UIElement[] { _name, _enabled, _stopProcessing, _matchMode, _condsPanel, _actionsPanel })
            e.IsEnabled = on;
    }

    UIElement ConditionRow(RuleCondition c)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var del = Btn("✕", (_, _) =>
        {
            if (_edit == null) return;
            Store();
            _edit.Conditions.Remove(c);
            Load(_edit);
        });
        del.Width = 28;
        del.Padding = new Thickness(0);
        DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(del);

        var fieldBox = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var f in RuleEngine.ConditionFields) fieldBox.Items.Add(f.Label);
        var curIdx = Array.FindIndex(RuleEngine.ConditionFields, x => x.Field == c.Field);
        fieldBox.SelectedIndex = curIdx >= 0 ? curIdx : 0;

        var valBox = new TextBox { Text = c.Value, VerticalAlignment = VerticalAlignment.Center };
        valBox.Visibility = RuleEngine.ConditionFields[fieldBox.SelectedIndex].NeedsValue ? Visibility.Visible : Visibility.Collapsed;

        fieldBox.SelectionChanged += (_, _) =>
        {
            var idx = fieldBox.SelectedIndex;
            if (idx >= 0 && idx < RuleEngine.ConditionFields.Length)
            {
                c.Field = RuleEngine.ConditionFields[idx].Field;
                valBox.Visibility = RuleEngine.ConditionFields[idx].NeedsValue ? Visibility.Visible : Visibility.Collapsed;
                Changed();
            }
        };
        valBox.TextChanged += (_, _) => { c.Value = valBox.Text; Changed(); };

        row.Children.Add(fieldBox);
        row.Children.Add(valBox);
        return row;
    }

    UIElement ActionRow(RuleAction a)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var del = Btn("✕", (_, _) =>
        {
            if (_edit == null) return;
            Store();
            _edit.Actions.Remove(a);
            Load(_edit);
        });
        del.Width = 28;
        del.Padding = new Thickness(0);
        DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(del);

        var typeBox = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var t in RuleEngine.ActionTypes) typeBox.Items.Add(t.Label);
        var curIdx = Array.FindIndex(RuleEngine.ActionTypes, x => x.Type == a.Type);
        typeBox.SelectedIndex = curIdx >= 0 ? curIdx : 0;

        var valBox = new TextBox { Text = a.Value ?? "", VerticalAlignment = VerticalAlignment.Center };
        valBox.Visibility = RuleEngine.ActionTypes[typeBox.SelectedIndex].NeedsValue ? Visibility.Visible : Visibility.Collapsed;

        typeBox.SelectionChanged += (_, _) =>
        {
            var idx = typeBox.SelectedIndex;
            if (idx >= 0 && idx < RuleEngine.ActionTypes.Length)
            {
                a.Type = RuleEngine.ActionTypes[idx].Type;
                valBox.Visibility = RuleEngine.ActionTypes[idx].NeedsValue ? Visibility.Visible : Visibility.Collapsed;
                Changed();
            }
        };
        valBox.TextChanged += (_, _) => { a.Value = valBox.Text; Changed(); };

        row.Children.Add(typeBox);
        row.Children.Add(valBox);
        return row;
    }

    void Changed()
    {
        if (_loading || _edit == null) return;
        TestCurrentRule();
    }

    void Store()
    {
        if (_edit == null) return;
        _edit.Name = string.IsNullOrWhiteSpace(_name.Text) ? L.T("Yeni kural") : _name.Text.Trim();
        _edit.Enabled = _enabled.IsChecked == true;
        _edit.StopProcessing = _stopProcessing.IsChecked == true;
        _edit.MatchAll = _matchMode.SelectedIndex == 0;
    }

    void TestCurrentRule()
    {
        if (_edit == null)
        {
            _matchInfo.Text = "";
            return;
        }
        Store();
        if (_sample.Count == 0)
        {
            _matchInfo.Text = L.T("Sınanacak örnek ileti yok.");
            return;
        }
        int matches = _sample.Count(h => RuleEngine.Matches(_edit, h));
        _matchInfo.Text = L.F("Örnek {0} iletiden {1} tanesi bu kurala uyuyor.", _sample.Count, matches);
    }

    async Task AiCreateAsync()
    {
        var text = _aiBox.Text.Trim();
        if (text.Length == 0) return;
        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        _aiButton.IsEnabled = false;
        _aiStatus.Text = L.F("{0} ile kural taslağı hazırlanıyor...", _providerName());
        try
        {
            var prompt = RuleEngine.AiPrompt(text);
            var response = await _ai(prompt, _aiCts.Token);
            var rule = RuleEngine.ParseAi(response);
            AddRule(rule);
            _aiStatus.Text = L.T("Kural oluşturuldu. İnceleyip 'Kaydet ve Kapat' ile onaylayın.");
            _aiBox.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _aiStatus.Text = L.T("Kural oluşturulamadı: ") + ex.Message;
        }
        finally
        {
            _aiButton.IsEnabled = true;
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views.QuickSteps;

/// <summary>
/// Dialog for managing Quick Steps (Hızlı Adımlar) macros.
/// </summary>
public sealed class QuickStepManagerWindow : Window
{
    readonly QuickStepStore _store;
    readonly TemplateStore _templateStore;
    readonly ListBox _stepListBox = new();
    readonly TextBox _nameBox = new();
    readonly ComboBox _shortcutBox = new();
    readonly ComboBox _iconBox = new();
    readonly ListBox _actionsListBox = new();
    readonly ComboBox _actionTypeBox = new();
    readonly TextBox _actionParamBox = new();
    readonly ComboBox _templatePickerBox = new();
    readonly StackPanel _paramStack = new();
    readonly TextBlock _paramLabel = new();
    readonly Button _saveBtn = new();
    readonly Button _deleteBtn = new();

    QuickStepItem? _selected;

    static readonly (string icon, string label)[] AvailableIcons =
    {
        ("\uE715", "Mektup / Yanıt"),
        ("\uE8B7", "Arşiv"),
        ("\uE7C1", "Bayrak"),
        ("\uE823", "Görev"),
        ("\uE8C3", "Okundu / Okunmadı"),
        ("\uE74D", "Sil"),
        ("\uE8DE", "Klasöre Taşı"),
        ("\uE8AC", "İmza / Not"),
        ("\uE787", "Toplantı"),
        ("\uE99A", "Yapay Zeka")
    };

    public QuickStepManagerWindow(QuickStepStore store, TemplateStore templateStore, Window? owner = null)
    {
        _store = store;
        _templateStore = templateStore;
        Owner = owner;
        Title = L.T("Hızlı Adımları Yönet");
        Width = 880;
        Height = 580;
        MinWidth = 720;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");

        BuildUi();
        RefreshList();
        SelectFirst();
    }

    void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var splitGrid = new Grid();
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 220 });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left pane
        var leftPanel = new DockPanel();

        var leftTop = new TextBlock
        {
            Text = L.T("Hızlı Adımlar:"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = ThemeManager.Brush("TextPrimaryBrush")
        };
        DockPanel.SetDock(leftTop, Dock.Top);
        leftPanel.Children.Add(leftTop);

        var newBtn = new Button
        {
            Content = "+ " + L.T("Yeni Hızlı Adım"),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.SemiBold
        };
        newBtn.Click += (_, _) => OnNewStep();
        DockPanel.SetDock(newBtn, Dock.Bottom);
        leftPanel.Children.Add(newBtn);

        _stepListBox.SelectionChanged += OnSelectionChanged;
        _stepListBox.BorderThickness = new Thickness(1);
        _stepListBox.BorderBrush = ThemeManager.Brush("BorderSubtleBrush");

        // Custom template for step item in list (icon + name + shortcut)
        var itemTemplate = new DataTemplate(typeof(QuickStepItem));
        var spFactory = new FrameworkElementFactory(typeof(StackPanel));
        spFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        spFactory.SetValue(StackPanel.MarginProperty, new Thickness(2, 4, 2, 4));

        var iconFactory = new FrameworkElementFactory(typeof(TextBlock));
        iconFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickStepItem.Icon)));
        iconFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets, Segoe UI Symbol"));
        iconFactory.SetValue(TextBlock.FontSizeProperty, 14.0);
        iconFactory.SetValue(TextBlock.WidthProperty, 24.0);
        iconFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        iconFactory.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("AccentHeaderBrush"));
        spFactory.AppendChild(iconFactory);

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickStepItem.DisplayName)));
        textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        spFactory.AppendChild(textFactory);

        var shortcutFactory = new FrameworkElementFactory(typeof(TextBlock));
        shortcutFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickStepItem.Shortcut)));
        shortcutFactory.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextMutedBrush"));
        shortcutFactory.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 0, 0));
        shortcutFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
        shortcutFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        spFactory.AppendChild(shortcutFactory);

        itemTemplate.VisualTree = spFactory;
        _stepListBox.ItemTemplate = itemTemplate;

        leftPanel.Children.Add(_stepListBox);
        splitGrid.Children.Add(leftPanel);
        Grid.SetColumn(leftPanel, 0);

        // Right pane: editor
        var rightBorder = new Border
        {
            Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"),
            BorderBrush = ThemeManager.Brush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14)
        };

        var rightGrid = new Grid();
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header (Name, Shortcut, Icon)
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Action controls (Add Action)
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Actions list
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Save / Delete

        // Row 0: Basic properties
        var propGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        propGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        propGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        propGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        propGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        propGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

        var nameStack = new StackPanel();
        nameStack.Children.Add(new TextBlock { Text = L.T("Hızlı Adım Adı:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        _nameBox.Padding = new Thickness(6, 4, 6, 4);
        nameStack.Children.Add(_nameBox);
        propGrid.Children.Add(nameStack);
        Grid.SetColumn(nameStack, 0);

        var iconStack = new StackPanel();
        iconStack.Children.Add(new TextBlock { Text = L.T("Simge:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        foreach (var (ic, label) in AvailableIcons)
        {
            _iconBox.Items.Add(new ComboBoxItem
            {
                Content = $"{ic}  {L.T(label)}",
                Tag = ic,
                FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI")
            });
        }
        _iconBox.SelectedIndex = 0;
        _iconBox.Padding = new Thickness(6, 3, 6, 3);
        iconStack.Children.Add(_iconBox);
        propGrid.Children.Add(iconStack);
        Grid.SetColumn(iconStack, 2);

        var scStack = new StackPanel();
        scStack.Children.Add(new TextBlock { Text = L.T("Kısayol") + ":", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        _shortcutBox.Items.Add(L.T("(Yok)"));
        for (int i = 1; i <= 9; i++)
        {
            _shortcutBox.Items.Add($"Ctrl+Shift+{i}");
        }
        _shortcutBox.SelectedIndex = 0;
        _shortcutBox.Padding = new Thickness(6, 3, 6, 3);
        scStack.Children.Add(_shortcutBox);
        propGrid.Children.Add(scStack);
        Grid.SetColumn(scStack, 4);

        rightGrid.Children.Add(propGrid);
        Grid.SetRow(propGrid, 0);

        // Row 1: Actions editor
        var actionSetupStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        actionSetupStack.Children.Add(new TextBlock { Text = L.T("Eylemler:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });

        var addActionGrid = new Grid();
        addActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        addActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        addActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        addActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Okundu olarak işaretle"), Tag = QuickStepActionType.MarkRead });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Okunmadı olarak işaretle"), Tag = QuickStepActionType.MarkUnread });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Bayrak ekle"), Tag = QuickStepActionType.Flag });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Bayrağı kaldır"), Tag = QuickStepActionType.Unflag });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Arşive taşı"), Tag = QuickStepActionType.MoveToArchive });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Klasöre taşı"), Tag = QuickStepActionType.MoveToFolder });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Sil"), Tag = QuickStepActionType.Delete });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Şablonla yanıtla"), Tag = QuickStepActionType.ReplyWithTemplate });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Görev oluştur"), Tag = QuickStepActionType.CreateTask });
        _actionTypeBox.Items.Add(new ComboBoxItem { Content = L.T("Kategori ata"), Tag = QuickStepActionType.SetCategory });
        _actionTypeBox.SelectedIndex = 0;
        _actionTypeBox.Padding = new Thickness(6, 3, 6, 3);
        _actionTypeBox.SelectionChanged += OnActionTypeChanged;
        addActionGrid.Children.Add(_actionTypeBox);
        Grid.SetColumn(_actionTypeBox, 0);

        _paramStack.Orientation = Orientation.Vertical;
        _actionParamBox.Padding = new Thickness(6, 4, 6, 4);
        _paramStack.Children.Add(_actionParamBox);
        _paramStack.Children.Add(_templatePickerBox);
        _templatePickerBox.Visibility = Visibility.Collapsed;
        _actionParamBox.Visibility = Visibility.Collapsed;
        _templatePickerBox.Padding = new Thickness(6, 3, 6, 3);

        foreach (var t in _templateStore.Templates)
        {
            _templatePickerBox.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.Id });
        }
        if (_templatePickerBox.Items.Count > 0) _templatePickerBox.SelectedIndex = 0;

        addActionGrid.Children.Add(_paramStack);
        Grid.SetColumn(_paramStack, 2);

        var addActionBtn = new Button
        {
            Content = L.T("+ Ekle"),
            Padding = new Thickness(8, 4, 8, 4),
            FontWeight = FontWeights.SemiBold
        };
        addActionBtn.Click += (_, _) => OnAddAction();
        addActionGrid.Children.Add(addActionBtn);
        Grid.SetColumn(addActionBtn, 4);

        actionSetupStack.Children.Add(addActionGrid);
        rightGrid.Children.Add(actionSetupStack);
        Grid.SetRow(actionSetupStack, 1);

        // Row 2: Actions list + order buttons
        var actionsDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

        var actBtns = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 0, 0, 0) };
        var actUpBtn = new Button { Content = "▲", Width = 30, Height = 28, Margin = new Thickness(0, 0, 0, 4), ToolTip = L.T("Yukarı taşı") };
        actUpBtn.Click += (_, _) => OnMoveAction(-1);
        actBtns.Children.Add(actUpBtn);

        var actDownBtn = new Button { Content = "▼", Width = 30, Height = 28, Margin = new Thickness(0, 0, 0, 4), ToolTip = L.T("Aşağı taşı") };
        actDownBtn.Click += (_, _) => OnMoveAction(1);
        actBtns.Children.Add(actDownBtn);

        var actRemoveBtn = new Button { Content = "✕", Width = 30, Height = 28, ToolTip = L.T("Eylemi kaldır") };
        actRemoveBtn.Click += (_, _) => OnRemoveAction();
        actBtns.Children.Add(actRemoveBtn);

        DockPanel.SetDock(actBtns, Dock.Right);
        actionsDock.Children.Add(actBtns);

        _actionsListBox.BorderThickness = new Thickness(1);
        _actionsListBox.BorderBrush = ThemeManager.Brush("BorderSubtleBrush");
        actionsDock.Children.Add(_actionsListBox);

        rightGrid.Children.Add(actionsDock);
        Grid.SetRow(actionsDock, 2);

        // Row 3: Save / Delete for right pane
        var editActions = new DockPanel();
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        _deleteBtn.Content = L.T("Sil");
        _deleteBtn.Padding = new Thickness(14, 5, 14, 5);
        _deleteBtn.Margin = new Thickness(0, 0, 8, 0);
        _deleteBtn.Click += (_, _) => OnDelete();
        rightBtns.Children.Add(_deleteBtn);

        _saveBtn.Content = L.T("Kaydet");
        _saveBtn.Padding = new Thickness(18, 5, 18, 5);
        _saveBtn.FontWeight = FontWeights.SemiBold;
        _saveBtn.Click += (_, _) => OnSave();
        rightBtns.Children.Add(_saveBtn);

        editActions.Children.Add(rightBtns);
        rightGrid.Children.Add(editActions);
        Grid.SetRow(editActions, 3);

        rightBorder.Child = rightGrid;
        splitGrid.Children.Add(rightBorder);
        Grid.SetColumn(rightBorder, 2);

        root.Children.Add(splitGrid);
        Grid.SetRow(splitGrid, 0);

        // Bottom Bar: Reset to Defaults + Close
        var bottomBar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };

        var resetBtn = new Button
        {
            Content = L.T("Varsayılanlara Sıfırla"),
            Padding = new Thickness(12, 5, 12, 5)
        };
        resetBtn.Click += (_, _) => OnResetDefaults();
        DockPanel.SetDock(resetBtn, Dock.Left);
        bottomBar.Children.Add(resetBtn);

        var closeBtn = new Button
        {
            Content = L.T("Kapat"),
            Padding = new Thickness(20, 5, 20, 5),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => Close();
        bottomBar.Children.Add(closeBtn);

        root.Children.Add(bottomBar);
        Grid.SetRow(bottomBar, 1);

        Content = root;
    }

    void OnActionTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_actionTypeBox.SelectedItem is not ComboBoxItem item || item.Tag is not QuickStepActionType actType)
            return;

        if (actType == QuickStepActionType.ReplyWithTemplate)
        {
            _templatePickerBox.Visibility = Visibility.Visible;
            _actionParamBox.Visibility = Visibility.Collapsed;
        }
        else if (actType is QuickStepActionType.MoveToFolder or QuickStepActionType.SetCategory or QuickStepActionType.CreateTask)
        {
            _actionParamBox.Visibility = Visibility.Visible;
            _templatePickerBox.Visibility = Visibility.Collapsed;
            _actionParamBox.ToolTip = actType switch
            {
                QuickStepActionType.MoveToFolder => L.T("Klasör Adı"),
                QuickStepActionType.SetCategory => L.T("Kategori Adı"),
                _ => L.T("Görev Notu / Kategorisi")
            };
        }
        else
        {
            _actionParamBox.Visibility = Visibility.Collapsed;
            _templatePickerBox.Visibility = Visibility.Collapsed;
        }
    }

    void RefreshList()
    {
        var steps = _store.Steps;
        _stepListBox.ItemsSource = steps;
        if (_selected != null)
        {
            _stepListBox.SelectedItem = steps.FirstOrDefault(s => s.Id == _selected.Id);
        }
    }

    void SelectFirst()
    {
        if (_stepListBox.Items.Count > 0 && _stepListBox.SelectedIndex < 0)
        {
            _stepListBox.SelectedIndex = 0;
        }
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_stepListBox.SelectedItem is QuickStepItem item)
        {
            _selected = item.Clone();
            LoadStep(_selected);
        }
        else if (_stepListBox.Items.Count == 0)
        {
            _selected = null;
            ClearForm();
        }
    }

    void LoadStep(QuickStepItem step)
    {
        _nameBox.Text = L.T(step.Name);

        // Icon
        int iconIdx = 0;
        for (int i = 0; i < AvailableIcons.Length; i++)
        {
            if (AvailableIcons[i].icon == step.Icon) { iconIdx = i; break; }
        }
        _iconBox.SelectedIndex = iconIdx;

        // Shortcut
        if (string.IsNullOrEmpty(step.Shortcut))
        {
            _shortcutBox.SelectedIndex = 0;
        }
        else
        {
            _shortcutBox.SelectedItem = step.Shortcut;
            if (_shortcutBox.SelectedIndex < 0) _shortcutBox.SelectedIndex = 0;
        }

        RefreshActionsList();
        _deleteBtn.IsEnabled = true;
        _saveBtn.IsEnabled = true;
    }

    void RefreshActionsList()
    {
        if (_selected == null)
        {
            _actionsListBox.ItemsSource = null;
            return;
        }

        var displayItems = _selected.Actions.Select(FormatActionDisplay).ToList();
        _actionsListBox.ItemsSource = displayItems;
        if (displayItems.Count > 0 && _actionsListBox.SelectedIndex < 0)
        {
            _actionsListBox.SelectedIndex = 0;
        }
    }

    string FormatActionDisplay(QuickStepAction act)
    {
        var typeText = act.Type switch
        {
            QuickStepActionType.MarkRead => L.T("Okundu olarak işaretle"),
            QuickStepActionType.MarkUnread => L.T("Okunmadı olarak işaretle"),
            QuickStepActionType.Flag => L.T("Bayrak ekle"),
            QuickStepActionType.Unflag => L.T("Bayrağı kaldır"),
            QuickStepActionType.MoveToArchive => L.T("Arşive taşı"),
            QuickStepActionType.MoveToFolder => L.T("Klasöre taşı: ") + act.Parameter,
            QuickStepActionType.Delete => L.T("Sil"),
            QuickStepActionType.ReplyWithTemplate => L.T("Şablonla yanıtla: ") + GetTemplateTitle(act.Parameter),
            QuickStepActionType.CreateTask => L.T("Görev oluştur") + (string.IsNullOrEmpty(act.Parameter) ? "" : $" ({act.Parameter})"),
            QuickStepActionType.SetCategory => L.T("Kategori ata: ") + act.Parameter,
            _ => act.Type.ToString()
        };
        return typeText;
    }

    string GetTemplateTitle(string? param)
    {
        if (string.IsNullOrEmpty(param)) return "";
        var t = _templateStore.FindById(param);
        return t != null ? t.Title : param;
    }

    void ClearForm()
    {
        _nameBox.Text = "";
        _shortcutBox.SelectedIndex = 0;
        _iconBox.SelectedIndex = 0;
        _actionsListBox.ItemsSource = null;
        _deleteBtn.IsEnabled = false;
        _saveBtn.IsEnabled = false;
    }

    void OnNewStep()
    {
        _selected = new QuickStepItem
        {
            Name = L.T("Yeni Hızlı Adım"),
            Icon = "\uE715",
            Actions = new List<QuickStepAction> { new() { Type = QuickStepActionType.MarkRead } }
        };
        _store.Save(_selected);
        RefreshList();
        _stepListBox.SelectedItem = _selected;
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    void OnAddAction()
    {
        if (_selected == null) return;
        if (_actionTypeBox.SelectedItem is not ComboBoxItem item || item.Tag is not QuickStepActionType actType)
            return;

        string? param = null;
        if (actType == QuickStepActionType.ReplyWithTemplate)
        {
            if (_templatePickerBox.SelectedItem is ComboBoxItem tItem)
                param = tItem.Tag?.ToString();
        }
        else if (actType is QuickStepActionType.MoveToFolder or QuickStepActionType.SetCategory or QuickStepActionType.CreateTask)
        {
            param = _actionParamBox.Text.Trim();
        }

        _selected.Actions.Add(new QuickStepAction { Type = actType, Parameter = param });
        RefreshActionsList();
        _actionsListBox.SelectedIndex = _selected.Actions.Count - 1;
    }

    void OnRemoveAction()
    {
        if (_selected == null || _actionsListBox.SelectedIndex < 0 || _actionsListBox.SelectedIndex >= _selected.Actions.Count)
            return;

        var idx = _actionsListBox.SelectedIndex;
        _selected.Actions.RemoveAt(idx);
        RefreshActionsList();
        if (_selected.Actions.Count > 0)
        {
            _actionsListBox.SelectedIndex = Math.Min(idx, _selected.Actions.Count - 1);
        }
    }

    void OnMoveAction(int delta)
    {
        if (_selected == null || _actionsListBox.SelectedIndex < 0) return;
        var idx = _actionsListBox.SelectedIndex;
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= _selected.Actions.Count) return;

        var item = _selected.Actions[idx];
        _selected.Actions.RemoveAt(idx);
        _selected.Actions.Insert(newIdx, item);
        RefreshActionsList();
        _actionsListBox.SelectedIndex = newIdx;
    }

    void OnSave()
    {
        if (_selected == null) return;

        var name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, L.T("Lütfen hızlı adım için bir isim girin."), L.T("Uyarı"), MessageBoxButton.OK, MessageBoxImage.Warning);
            _nameBox.Focus();
            return;
        }

        _selected.Name = name;
        if (_iconBox.SelectedItem is ComboBoxItem iconItem && iconItem.Tag is string ic)
        {
            _selected.Icon = ic;
        }

        var sc = _shortcutBox.SelectedItem?.ToString();
        _selected.Shortcut = (sc == null || sc.Contains("Yok")) ? "" : sc;

        _store.Save(_selected);
        RefreshList();
    }

    void OnDelete()
    {
        if (_selected == null) return;

        var res = MessageBox.Show(this,
            L.F("\"{0}\" hızlı adımını silmek istediğinizden emin misiniz?", _selected.Name),
            L.T("Hızlı Adımı Sil"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        _store.Delete(_selected.Id);
        _selected = null;
        RefreshList();
        SelectFirst();
    }

    void OnResetDefaults()
    {
        var res = MessageBox.Show(this,
            L.T("Tüm hızlı adımlar varsayılan durumlarına döndürülecek. Devam edilsin mi?"),
            L.T("Varsayılanlara Sıfırla"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        _store.ResetToDefaults();
        _selected = null;
        RefreshList();
        SelectFirst();
    }
}

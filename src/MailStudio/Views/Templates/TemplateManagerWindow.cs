using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;
using Microsoft.Win32;

namespace MailStudio.Views.Templates;

/// <summary>
/// Template Manager dialog for viewing, editing, creating, importing, and exporting email templates.
/// </summary>
public sealed class TemplateManagerWindow : Window
{
    readonly TemplateStore _store;
    readonly ListBox _listBox = new();
    readonly TextBox _searchBox = new();
    readonly TextBox _titleBox = new();
    readonly TextBox _categoryBox = new();
    readonly ComboBox _langBox = new();
    readonly TextBox _subjectBox = new();
    readonly TextBox _bodyBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Button _saveBtn = new();
    readonly Button _deleteBtn = new();
    readonly TextBlock _statusText = new();

    EmailTemplate? _selected;

    public TemplateManagerWindow(TemplateStore store, Window? owner = null)
    {
        _store = store;
        Owner = owner;
        Title = L.T("Hazır Yanıt Şablonları");
        Width = 920;
        Height = 620;
        MinWidth = 760;
        MinHeight = 500;
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

        // Left pane: search + list + new button
        var leftPanel = new DockPanel();

        var leftTop = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var searchLbl = new TextBlock { Text = L.T("Şablon Ara:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") };
        _searchBox.Padding = new Thickness(6, 4, 6, 4);
        _searchBox.TextChanged += (_, _) => RefreshList();
        leftTop.Children.Add(searchLbl);
        leftTop.Children.Add(_searchBox);

        DockPanel.SetDock(leftTop, Dock.Top);
        leftPanel.Children.Add(leftTop);

        var newBtn = new Button
        {
            Content = "+ " + L.T("Yeni Şablon"),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.SemiBold
        };
        newBtn.Click += (_, _) => OnNewTemplate();
        DockPanel.SetDock(newBtn, Dock.Bottom);
        leftPanel.Children.Add(newBtn);

        _listBox.SelectionChanged += OnSelectionChanged;
        _listBox.DisplayMemberPath = nameof(EmailTemplate.Title);
        _listBox.BorderThickness = new Thickness(1);
        _listBox.BorderBrush = ThemeManager.Brush("BorderSubtleBrush");
        leftPanel.Children.Add(_listBox);

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
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title & category
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // subject
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // variables toolbar
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // save / delete

        // Row 0: Title, Category, Language
        var headerPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock { Text = L.T("Şablon Başlığı:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        _titleBox.Padding = new Thickness(6, 4, 6, 4);
        titleStack.Children.Add(_titleBox);
        headerPanel.Children.Add(titleStack);
        Grid.SetColumn(titleStack, 0);

        var catStack = new StackPanel();
        catStack.Children.Add(new TextBlock { Text = L.T("Kategori:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        _categoryBox.Padding = new Thickness(6, 4, 6, 4);
        catStack.Children.Add(_categoryBox);
        headerPanel.Children.Add(catStack);
        Grid.SetColumn(catStack, 2);

        var langStack = new StackPanel();
        langStack.Children.Add(new TextBlock { Text = L.T("Dil:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        _langBox.Items.Add("tr");
        _langBox.Items.Add("en");
        _langBox.Items.Add("all");
        _langBox.SelectedIndex = 0;
        _langBox.Padding = new Thickness(6, 3, 6, 3);
        langStack.Children.Add(_langBox);
        headerPanel.Children.Add(langStack);
        Grid.SetColumn(langStack, 4);

        rightGrid.Children.Add(headerPanel);
        Grid.SetRow(headerPanel, 0);

        // Row 1: Subject (optional)
        var subjStack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        subjStack.Children.Add(new TextBlock { Text = L.T("Konu Şablonu (İsteğe bağlı):"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        _subjectBox.Padding = new Thickness(6, 4, 6, 4);
        subjStack.Children.Add(_subjectBox);
        rightGrid.Children.Add(subjStack);
        Grid.SetRow(subjStack, 1);

        // Row 2: Variable buttons toolbar
        var varStack = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var varLbl = new TextBlock
        {
            Text = L.T("Değişkenler:"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = ThemeManager.Brush("TextPrimaryBrush")
        };
        DockPanel.SetDock(varLbl, Dock.Left);
        varStack.Children.Add(varLbl);

        var varWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        AddVarButton(varWrap, "{Ad}", L.T("Alıcı Adı"));
        AddVarButton(varWrap, "{Soyad}", L.T("Alıcı Soyadı"));
        AddVarButton(varWrap, "{Firma}", L.T("Firma"));
        AddVarButton(varWrap, "{Konu}", L.T("Konu"));
        AddVarButton(varWrap, "{ParçaNo}", L.T("Parça No"));
        AddVarButton(varWrap, "{Tarih}", L.T("Tarih"));
        AddVarButton(varWrap, "{İmza}", L.T("İmza"));
        varStack.Children.Add(varWrap);

        rightGrid.Children.Add(varStack);
        Grid.SetRow(varStack, 2);

        // Row 3: Body
        var bodyStack = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var bodyLbl = new TextBlock
        {
            Text = L.T("İleti Gövdesi:"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = ThemeManager.Brush("TextPrimaryBrush")
        };
        DockPanel.SetDock(bodyLbl, Dock.Top);
        bodyStack.Children.Add(bodyLbl);
        _bodyBox.Padding = new Thickness(6, 6, 6, 6);
        _bodyBox.FontFamily = new FontFamily("Calibri, Segoe UI");
        _bodyBox.FontSize = 13;
        bodyStack.Children.Add(_bodyBox);

        rightGrid.Children.Add(bodyStack);
        Grid.SetRow(bodyStack, 3);

        // Row 4: Action buttons for editor
        var editActions = new DockPanel();
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.Foreground = ThemeManager.Brush("TextSecondaryBrush");
        DockPanel.SetDock(_statusText, Dock.Left);
        editActions.Children.Add(_statusText);

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
        Grid.SetRow(editActions, 4);

        rightBorder.Child = rightGrid;
        splitGrid.Children.Add(rightBorder);
        Grid.SetColumn(rightBorder, 2);

        root.Children.Add(splitGrid);
        Grid.SetRow(splitGrid, 0);

        // Bottom Bar: Import/Export + Close
        var bottomBar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };

        var ioPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var importBtn = new Button
        {
            Content = L.T("İçe Aktar (JSON)…"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        importBtn.Click += (_, _) => OnImport();
        ioPanel.Children.Add(importBtn);

        var exportBtn = new Button
        {
            Content = L.T("Dışa Aktar (JSON)…"),
            Padding = new Thickness(12, 5, 12, 5)
        };
        exportBtn.Click += (_, _) => OnExport();
        ioPanel.Children.Add(exportBtn);

        DockPanel.SetDock(ioPanel, Dock.Left);
        bottomBar.Children.Add(ioPanel);

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

    void AddVarButton(WrapPanel panel, string tag, string tooltip)
    {
        var btn = new Button
        {
            Content = tag,
            ToolTip = tooltip,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(2, 2, 2, 2),
            FontSize = 11.5,
            FontFamily = new FontFamily("Consolas, Courier New")
        };
        btn.Click += (_, _) =>
        {
            var idx = _bodyBox.SelectionStart;
            var curText = _bodyBox.Text ?? "";
            _bodyBox.Text = curText.Insert(idx, tag);
            _bodyBox.SelectionStart = idx + tag.Length;
            _bodyBox.Focus();
        };
        panel.Children.Add(btn);
    }

    void RefreshList()
    {
        var query = _searchBox.Text.Trim();
        var all = _store.Templates;
        var filtered = string.IsNullOrEmpty(query)
            ? all
            : all.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             t.Body.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             t.Category.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _listBox.ItemsSource = filtered;
        if (_selected != null)
        {
            _listBox.SelectedItem = filtered.FirstOrDefault(t => t.Id == _selected.Id);
        }
    }

    void SelectFirst()
    {
        if (_listBox.Items.Count > 0 && _listBox.SelectedIndex < 0)
        {
            _listBox.SelectedIndex = 0;
        }
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_listBox.SelectedItem is EmailTemplate t)
        {
            _selected = t;
            LoadTemplate(t);
        }
        else if (_listBox.Items.Count == 0)
        {
            _selected = null;
            ClearForm();
        }
    }

    void LoadTemplate(EmailTemplate t)
    {
        _titleBox.Text = t.Title;
        _categoryBox.Text = t.Category;
        _subjectBox.Text = t.Subject;
        _bodyBox.Text = t.Body;

        _langBox.SelectedItem = t.Language switch
        {
            "en" => "en",
            "all" => "all",
            _ => "tr"
        };

        _statusText.Text = t.IsBuiltIn ? L.T("Varsayılan şablon") : "";
        _deleteBtn.IsEnabled = true;
        _saveBtn.IsEnabled = true;
    }

    void ClearForm()
    {
        _titleBox.Text = "";
        _categoryBox.Text = "";
        _subjectBox.Text = "";
        _bodyBox.Text = "";
        _langBox.SelectedIndex = 0;
        _statusText.Text = "";
        _deleteBtn.IsEnabled = false;
        _saveBtn.IsEnabled = false;
    }

    void OnNewTemplate()
    {
        _selected = new EmailTemplate
        {
            Title = L.T("Yeni Şablon"),
            Category = L.T("Genel"),
            Language = "tr",
            Body = L.T("Sayın {Ad},\n\n\n\nSaygılarımızla,\n{İmza}")
        };
        _store.Save(_selected);
        RefreshList();
        _listBox.SelectedItem = _selected;
        _titleBox.Focus();
        _titleBox.SelectAll();
    }

    void OnSave()
    {
        if (_selected == null)
        {
            _selected = new EmailTemplate();
        }

        var title = _titleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.Show(this, L.T("Lütfen şablon için bir başlık girin."), L.T("Uyarı"), MessageBoxButton.OK, MessageBoxImage.Warning);
            _titleBox.Focus();
            return;
        }

        _selected.Title = title;
        _selected.Category = string.IsNullOrWhiteSpace(_categoryBox.Text) ? "Genel" : _categoryBox.Text.Trim();
        _selected.Language = _langBox.SelectedItem?.ToString() ?? "tr";
        _selected.Subject = _subjectBox.Text.Trim();
        _selected.Body = _bodyBox.Text;

        _store.Save(_selected);
        _statusText.Text = L.T("Kaydedildi.");
        RefreshList();
    }

    void OnDelete()
    {
        if (_selected == null) return;

        var res = MessageBox.Show(this,
            L.F("\"{0}\" şablonunu silmek istediğinizden emin misiniz?", _selected.Title),
            L.T("Şablonu Sil"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        _store.Delete(_selected.Id);
        _selected = null;
        RefreshList();
        SelectFirst();
    }

    void OnExport()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            FileName = "mailstudio-templates.json",
            Title = L.T("Şablonları Dışa Aktar")
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, _store.ExportJson());
                MessageBox.Show(this, L.T("Şablonlar başarıyla dışa aktarıldı."), L.T("Dışa Aktarma"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Dışa aktarma hatası: ") + ex.Message, L.T("Hata"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    void OnImport()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            Title = L.T("Şablonları İçe Aktar")
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var json = File.ReadAllText(dlg.FileName);
                var count = _store.ImportJson(json);
                RefreshList();
                SelectFirst();
                MessageBox.Show(this, L.F("{0} adet şablon içeri aktarıldı.", count), L.T("İçe Aktarma"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("İçe aktarma hatası: ") + ex.Message, L.T("Hata"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

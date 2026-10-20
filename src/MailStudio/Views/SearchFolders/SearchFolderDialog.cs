using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views.SearchFolders;

public sealed class SearchFolderDialog : Window
{
    readonly TextBox _nameBox;
    readonly TextBox _fromBox;
    readonly TextBox _queryBox;
    readonly TextBox _categoryBox;
    readonly ComboBox _attCombo;
    readonly ComboBox _attTypeCombo;
    readonly ComboBox _dateRangeCombo;
    readonly CheckBox _unreadCheck;
    readonly CheckBox _flaggedCheck;

    public SearchFolderItem? ResultItem { get; private set; }

    public SearchFolderDialog(SearchFolderItem? existing = null)
    {
        Title = existing == null ? L.T("Yeni Arama Klasörü") : L.T("Arama Klasörünü Düzenle");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfaceBrush");
        Foreground = ThemeManager.Brush("TextPrimaryBrush");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Form
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

        // Header
        var header = new TextBlock
        {
            Text = Title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.Brush("AccentHeaderBrush"),
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Form
        var form = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        // Name
        form.Children.Add(new TextBlock { Text = L.T("Klasör Adı"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _nameBox = new TextBox
        {
            Text = existing?.Name ?? "",
            Height = 28,
            Margin = new Thickness(0, 0, 0, 12),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        form.Children.Add(_nameBox);

        // From
        form.Children.Add(new TextBlock { Text = L.T("Kimden (gönderen e-posta veya ad)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _fromBox = new TextBox
        {
            Text = existing?.Criteria.From ?? "",
            Height = 28,
            Margin = new Thickness(0, 0, 0, 12),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        form.Children.Add(_fromBox);

        // Query
        form.Children.Add(new TextBlock { Text = L.T("Arama Metni (konu veya önizlemede geçen kelimeler)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _queryBox = new TextBox
        {
            Text = existing?.Criteria.Query ?? "",
            Height = 28,
            Margin = new Thickness(0, 0, 0, 12),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        form.Children.Add(_queryBox);

        // Attachments
        var attGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        attGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        attGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        attGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var attCol1 = new StackPanel();
        attCol1.Children.Add(new TextBlock { Text = L.T("Ek Durumu"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _attCombo = new ComboBox { Height = 28 };
        _attCombo.Items.Add(new ComboBoxItem { Content = L.T("Fark etmez"), Tag = "" });
        _attCombo.Items.Add(new ComboBoxItem { Content = L.T("Yalnızca ekli mailler"), Tag = "yes" });
        _attCombo.Items.Add(new ComboBoxItem { Content = L.T("Eksiz mailler"), Tag = "no" });
        if (existing?.Criteria.HasAttachments == true) _attCombo.SelectedIndex = 1;
        else if (existing?.Criteria.HasAttachments == false) _attCombo.SelectedIndex = 2;
        else _attCombo.SelectedIndex = 0;
        attCol1.Children.Add(_attCombo);
        Grid.SetColumn(attCol1, 0);
        attGrid.Children.Add(attCol1);

        var attCol2 = new StackPanel();
        attCol2.Children.Add(new TextBlock { Text = L.T("Ek Türü"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _attTypeCombo = new ComboBox { Height = 28 };
        _attTypeCombo.Items.Add(new ComboBoxItem { Content = L.T("Tümü"), Tag = "" });
        _attTypeCombo.Items.Add(new ComboBoxItem { Content = "PDF (.pdf)", Tag = "pdf" });
        _attTypeCombo.Items.Add(new ComboBoxItem { Content = "DXF / DWG (.dxf, .dwg)", Tag = "dxf" });
        _attTypeCombo.Items.Add(new ComboBoxItem { Content = "STEP / 3D CAD (.stp, .step)", Tag = "stp" });
        _attTypeCombo.Items.Add(new ComboBoxItem { Content = "Excel (.xlsx, .xls)", Tag = "xlsx" });

        var curType = existing?.Criteria.AttachmentType ?? "";
        int typeIdx = 0;
        for (int i = 1; i < _attTypeCombo.Items.Count; i++)
        {
            if (string.Equals((_attTypeCombo.Items[i] as ComboBoxItem)?.Tag as string, curType, StringComparison.OrdinalIgnoreCase))
            {
                typeIdx = i;
                break;
            }
        }
        _attTypeCombo.SelectedIndex = typeIdx;
        attCol2.Children.Add(_attTypeCombo);
        Grid.SetColumn(attCol2, 2);
        attGrid.Children.Add(attCol2);
        form.Children.Add(attGrid);

        // Date Range
        form.Children.Add(new TextBlock { Text = L.T("Tarih Aralığı"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _dateRangeCombo = new ComboBox { Height = 28, Margin = new Thickness(0, 0, 0, 12) };
        _dateRangeCombo.Items.Add(new ComboBoxItem { Content = L.T("Tümü"), Tag = "" });
        _dateRangeCombo.Items.Add(new ComboBoxItem { Content = L.T("Bugün"), Tag = "today" });
        _dateRangeCombo.Items.Add(new ComboBoxItem { Content = L.T("Bu hafta"), Tag = "this_week" });
        _dateRangeCombo.Items.Add(new ComboBoxItem { Content = L.T("Bu ay"), Tag = "this_month" });
        _dateRangeCombo.Items.Add(new ComboBoxItem { Content = L.T("Son 30 gün"), Tag = "last_30_days" });

        var curRange = existing?.Criteria.DateRangePreset ?? "";
        int rangeIdx = 0;
        for (int i = 1; i < _dateRangeCombo.Items.Count; i++)
        {
            if (string.Equals((_dateRangeCombo.Items[i] as ComboBoxItem)?.Tag as string, curRange, StringComparison.OrdinalIgnoreCase))
            {
                rangeIdx = i;
                break;
            }
        }
        _dateRangeCombo.SelectedIndex = rangeIdx;
        form.Children.Add(_dateRangeCombo);

        // Checkboxes: unread & flagged
        _unreadCheck = new CheckBox
        {
            Content = L.T("Yalnızca okunmamış mailler"),
            IsChecked = existing?.Criteria.IsUnread ?? false,
            Margin = new Thickness(0, 0, 0, 8)
        };
        form.Children.Add(_unreadCheck);

        _flaggedCheck = new CheckBox
        {
            Content = L.T("Yalnızca bayraklı mailler"),
            IsChecked = existing?.Criteria.IsFlagged ?? false,
            Margin = new Thickness(0, 0, 0, 12)
        };
        form.Children.Add(_flaggedCheck);

        _categoryBox = new TextBox
        {
            Text = existing?.Criteria.Category ?? "",
            Visibility = Visibility.Collapsed
        };
        form.Children.Add(_categoryBox);

        Grid.SetRow(form, 1);
        root.Children.Add(form);

        // Buttons
        var btnBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var okBtn = new Button
        {
            Content = L.T("Kaydet"),
            Width = 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okBtn.SetResourceReference(StyleProperty, "PrimaryButton");
        okBtn.Click += (_, _) =>
        {
            var name = _nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = L.T("Özel Arama");
            }

            var item = existing?.Clone() ?? new SearchFolderItem();
            item.Name = name;
            item.Criteria.From = string.IsNullOrWhiteSpace(_fromBox.Text) ? null : _fromBox.Text.Trim();
            item.Criteria.Query = string.IsNullOrWhiteSpace(_queryBox.Text) ? null : _queryBox.Text.Trim();

            var attTag = (_attCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            item.Criteria.HasAttachments = attTag switch
            {
                "yes" => true,
                "no" => false,
                _ => null
            };

            var typeTag = (_attTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            item.Criteria.AttachmentType = string.IsNullOrEmpty(typeTag) ? null : typeTag;

            var dateTag = (_dateRangeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            item.Criteria.DateRangePreset = string.IsNullOrEmpty(dateTag) ? null : dateTag;

            item.Criteria.IsUnread = _unreadCheck.IsChecked == true ? true : null;
            item.Criteria.IsFlagged = _flaggedCheck.IsChecked == true ? true : null;
            item.Criteria.Category = string.IsNullOrWhiteSpace(_categoryBox.Text) ? null : _categoryBox.Text.Trim();

            ResultItem = item;
            try { if (ComponentDispatcher.IsThreadModal) DialogResult = true; } catch { }
            Close();
        };

        var cancelBtn = new Button
        {
            Content = L.T("İptal"),
            Width = 90,
            Height = 32,
            IsCancel = true
        };
        cancelBtn.SetResourceReference(StyleProperty, "FlatButton");
        cancelBtn.Click += (_, _) =>
        {
            try { if (ComponentDispatcher.IsThreadModal) DialogResult = false; } catch { }
            Close();
        };

        btnBar.Children.Add(okBtn);
        btnBar.Children.Add(cancelBtn);
        Grid.SetRow(btnBar, 2);
        root.Children.Add(btnBar);

        Content = root;
        UiTranslator.Apply(this);
    }

    public static SearchFolderItem? ShowDialog(Window? owner, SearchFolderItem? existing = null)
    {
        var dlg = new SearchFolderDialog(existing);
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        var res = dlg.ShowDialog();
        return res == true ? dlg.ResultItem : null;
    }
}

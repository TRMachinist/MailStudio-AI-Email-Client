using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Views.Calendar;

namespace MailStudio.Views.Quotes;

public sealed class QuoteEditDialog : Window
{
    readonly QuoteItem _item;
    readonly TextBox _customer = new() { Height = 28 };
    readonly TextBox _contact = new() { Height = 28 };
    readonly TextBox _email = new() { Height = 28 };
    readonly TextBox _partNumber = new() { Height = 28, FontWeight = FontWeights.SemiBold };
    readonly TextBox _revision = new() { Height = 28, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox _quantity = new() { Height = 28, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox _material = new() { Height = 28 };
    readonly DatePicker _deadline = new() { Height = 28, Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
    readonly DatePicker _requestedDelivery = new() { Height = 28, Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox _status = new() { Height = 28, Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox _notes = new() { MinHeight = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public QuoteItem Item => _item;

    public QuoteEditDialog(QuoteItem item, bool isNew = false)
    {
        _item = item.Clone();
        UiTranslator.SetSkip(this, true);

        Title = (isNew ? L.T("Yeni Teklif Talebi") : L.T("Teklifi Düzenle")) + " - " + L.ProductName;
        Width = 620; Height = 560; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        // Populate fields
        _customer.Text = _item.Customer;
        _contact.Text = _item.Contact;
        _email.Text = _item.ContactEmail ?? "";
        _partNumber.Text = _item.PartNumber;
        _revision.Text = _item.Revision;
        _quantity.Text = _item.Quantity > 0 ? _item.Quantity.ToString(CultureInfo.InvariantCulture) : "";
        _material.Text = _item.Material;
        _deadline.SelectedDate = _item.Deadline;
        _requestedDelivery.SelectedDate = _item.RequestedDelivery;
        _notes.Text = _item.Notes;

        foreach (QuoteStatus st in Enum.GetValues(typeof(QuoteStatus)))
        {
            var cbi = new ComboBoxItem
            {
                Content = st switch
                {
                    QuoteStatus.New => L.T("Yeni"),
                    QuoteStatus.InProgress => L.T("Hazırlanıyor"),
                    QuoteStatus.Sent => L.T("Gönderildi"),
                    QuoteStatus.Won => L.T("Kazanıldı"),
                    QuoteStatus.Lost => L.T("Kaybedildi"),
                    _ => st.ToString()
                },
                Tag = st
            };
            _status.Items.Add(cbi);
            if (st == _item.Status) _status.SelectedItem = cbi;
        }
        if (_status.SelectedIndex < 0) _status.SelectedIndex = 0;

        var root = new DockPanel();

        // Footer buttons
        var footer = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        footer.SetResourceReference(Panel.BackgroundProperty, "BgSurfaceSecondaryBrush");

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var saveBtn = new Button { Content = L.T("Kaydet"), Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        saveBtn.SetResourceReference(StyleProperty, "RibbonPrimaryCmd");
        saveBtn.Click += (_, _) => SaveAndClose();

        var cancelBtn = new Button { Content = L.T("İptal"), Width = 80, Height = 30, IsCancel = true };
        cancelBtn.SetResourceReference(StyleProperty, "RibbonCmd");
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);
        footer.Child = btnPanel;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Header banner
        var header = new Border
        {
            Padding = new Thickness(18, 14, 18, 14),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        header.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        header.SetResourceReference(Panel.BackgroundProperty, "BgSurfaceSecondaryBrush");

        var headPanel = new StackPanel();
        var headTitle = new TextBlock
        {
            Text = isNew ? L.T("Yeni Teklif Masası Kaydı") : L.T("Teklif Masası Kaydı Düzenle"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };
        headTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        headPanel.Children.Add(headTitle);

        if (!string.IsNullOrEmpty(_item.SourceSubject))
        {
            var mailSub = new TextBlock
            {
                Text = L.T("Kaynak İleti: ") + _item.SourceSubject,
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            mailSub.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            headPanel.Children.Add(mailSub);
        }
        header.Child = headPanel;
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Form body in a scroll viewer
        var formScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var formGrid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int r = 0;
        void AddRow(string label, FrameworkElement control)
        {
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 12, 5),
                FontWeight = FontWeights.Medium
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0); formGrid.Children.Add(lbl);

            control.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(control, r); Grid.SetColumn(control, 1); formGrid.Children.Add(control);
            r++;
        }

        AddRow(L.T("Müşteri / Firma:"), _customer);
        AddRow(L.T("İlgili Kişi:"), _contact);
        AddRow(L.T("E-posta:"), _email);
        AddRow(L.T("Parça Numarası:"), _partNumber);

        var revQtyPanel = new StackPanel { Orientation = Orientation.Horizontal };
        revQtyPanel.Children.Add(_revision);
        var qtyLbl = new TextBlock
        {
            Text = L.T("Adet:"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 8, 0),
            FontWeight = FontWeights.Medium
        };
        qtyLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        revQtyPanel.Children.Add(qtyLbl);
        revQtyPanel.Children.Add(_quantity);
        AddRow(L.T("Revizyon:"), revQtyPanel);

        AddRow(L.T("Malzeme:"), _material);
        AddRow(L.T("Son Teklif Tarihi:"), _deadline);
        AddRow(L.T("İstenen Teslim:"), _requestedDelivery);
        AddRow(L.T("Durum:"), _status);
        AddRow(L.T("Notlar:"), _notes);

        formScroll.Content = formGrid;
        root.Children.Add(formScroll);

        Content = root;
        Loaded += (_, _) => _customer.Focus();
    }

    void SaveAndClose()
    {
        _item.Customer = _customer.Text.Trim();
        _item.Contact = _contact.Text.Trim();
        _item.ContactEmail = _email.Text.Trim();
        _item.PartNumber = _partNumber.Text.Trim();
        _item.Revision = _revision.Text.Trim();

        if (int.TryParse(_quantity.Text.Trim(), out var q) && q >= 0)
            _item.Quantity = q;
        else
            _item.Quantity = 0;

        _item.Material = _material.Text.Trim();
        _item.Deadline = _deadline.SelectedDate;
        _item.RequestedDelivery = _requestedDelivery.SelectedDate;

        if (_status.SelectedItem is ComboBoxItem { Tag: QuoteStatus st })
            _item.Status = st;

        _item.Notes = _notes.Text.Trim();
        _item.UpdatedAt = DateTimeOffset.Now;

        DialogResult = true;
        Close();
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Views.Calendar;
using Microsoft.Win32;

namespace MailStudio.Views.Quotes;

public interface IQuoteHost : IPimHost
{
    void ComposeQuoteReply(QuoteItem quote);
}

public enum QuoteFilter
{
    All,
    Active,
    New,
    InProgress,
    Sent,
    Won,
    Lost,
    Overdue
}

public sealed class QuoteDeskView : UserControl
{
    readonly QuoteStore _store;
    readonly IQuoteHost _host;
    readonly ListView _list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _search = new() { Margin = new Thickness(10, 8, 10, 8), Padding = new Thickness(6, 4, 6, 4) };
    readonly TextBlock _searchPlaceholder = PimUi.Text("", 12, brush: "TextMutedBrush");
    readonly StackPanel _filterButtons = new();
    readonly TextBlock _kpiTotal = PimUi.Text("0", 16, bold: true);
    readonly TextBlock _kpiActive = PimUi.Text("0", 16, bold: true, brush: "AccentPrimaryBrush");
    readonly TextBlock _kpiOverdue = PimUi.Text("0", 16, bold: true);
    readonly TextBlock _kpiWon = PimUi.Text("0", 16, bold: true);
    readonly TextBlock _emptyHint = PimUi.Text("", 14, brush: "TextMutedBrush");

    QuoteFilter _filter = QuoteFilter.All;
    string _sortColumn = "CreatedAt";
    bool _sortAsc = false;
    List<QuoteItem> _currentView = new();

    public WrapPanel Ribbon { get; } = new() { Orientation = Orientation.Horizontal };
    public FrameworkElement SidePanel { get; }

    public QuoteDeskView(QuoteStore store, IQuoteHost host)
    {
        _store = store;
        _host = host;
        UiTranslator.SetSkip(this, true);
        UiTranslator.SetSkip(Ribbon, true);

        // 1. Ribbon
        Ribbon.Children.Add(PimUi.Group(L.T("Yeni"),
            PimUi.Cmd("", L.T("Yeni Teklif"), (_, _) => OnNewQuote(), L.T("Yeni teklif talebi ekle (Ctrl+N)"), primary: true)));

        Ribbon.Children.Add(PimUi.Group(L.T("Yönet"),
            PimUi.Cmd("", L.T("Düzenle"), (_, _) => OnEditSelected(), L.T("Teklifi düzenle")),
            PimUi.Cmd("", L.T("Sil"), (_, _) => OnDeleteSelected(), L.T("Teklifi sil (Delete)"))));

        Ribbon.Children.Add(PimUi.Group(L.T("Durum"),
            PimUi.Cmd("", L.T("Yeni"), (_, _) => SetSelectedStatus(QuoteStatus.New), L.T("Durumu 'Yeni' yap")),
            PimUi.Cmd("", L.T("Hazırlanıyor"), (_, _) => SetSelectedStatus(QuoteStatus.InProgress), L.T("Durumu 'Hazırlanıyor' yap")),
            PimUi.Cmd("", L.T("Gönderildi"), (_, _) => SetSelectedStatus(QuoteStatus.Sent), L.T("Durumu 'Gönderildi' yap")),
            PimUi.Cmd("", L.T("Kazanıldı"), (_, _) => SetSelectedStatus(QuoteStatus.Won), L.T("Durumu 'Kazanıldı' yap")),
            PimUi.Cmd("", L.T("Kaybedildi"), (_, _) => SetSelectedStatus(QuoteStatus.Lost), L.T("Durumu 'Kaybedildi' yap"))));

        Ribbon.Children.Add(PimUi.Group(L.T("Dışa Aktar"),
            PimUi.Cmd("", L.T("Excel'e Aktar"), (_, _) => OnExportExcel(), L.T("Teklifleri Excel (.xlsx) dosyasına aktar")),
            PimUi.Cmd("", L.T("Yenile"), (_, _) => Refresh(), L.T("Listeyi yenile"))));

        Ribbon.Children.Add(PimUi.Group(L.T("İletişim"),
            PimUi.Cmd("", L.T("Teklif Yanıtı"), (_, _) => OnReplyQuote(), L.T("Müşteriye teklif e-postası taslağı aç")),
            PimUi.Cmd("", L.T("Maili Aç"), (_, _) => OnOpenSourceMail(), L.T("Teklifin kaynak e-postasını aç"))));

        // 2. Side Panel
        var side = new StackPanel();
        var sideTitle = PimUi.Text(L.T("Teklif Masası"), 13, bold: true);
        sideTitle.Margin = new Thickness(12, 10, 8, 4);
        side.Children.Add(sideTitle);

        // Search in side panel
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        _searchPlaceholder.Text = L.T("Ara (parça, müşteri, not)...");
        _searchPlaceholder.IsHitTestVisible = false;
        _searchPlaceholder.Margin = new Thickness(18, 14, 12, 8);
        searchHost.Children.Add(_searchPlaceholder);
        _search.TextChanged += (_, _) =>
        {
            _searchPlaceholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        };
        side.Children.Add(searchHost);

        // Filter buttons
        var filters = new (QuoteFilter Filter, string Label, string Glyph)[]
        {
            (QuoteFilter.All, L.T("Tümü"), ""),
            (QuoteFilter.Active, L.T("Aktif Teklifler"), ""),
            (QuoteFilter.New, L.T("Yeni"), ""),
            (QuoteFilter.InProgress, L.T("Hazırlanıyor"), ""),
            (QuoteFilter.Sent, L.T("Gönderildi"), ""),
            (QuoteFilter.Won, L.T("Kazanıldı"), ""),
            (QuoteFilter.Lost, L.T("Kaybedildi"), ""),
            (QuoteFilter.Overdue, L.T("Geciken Teklifler"), ""),
        };

        foreach (var (f, label, glyph) in filters)
        {
            var btn = PimUi.Cmd(glyph, label, (_, _) =>
            {
                _filter = f;
                Refresh();
            });
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.HorizontalContentAlignment = HorizontalAlignment.Left;
            btn.Margin = new Thickness(6, 1, 6, 1);
            btn.DataContext = f;
            _filterButtons.Children.Add(btn);
        }
        side.Children.Add(_filterButtons);

        // KPI Box
        var kpiBorder = new Border
        {
            Margin = new Thickness(10, 16, 10, 8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        kpiBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        kpiBorder.SetResourceReference(Panel.BackgroundProperty, "BgSurfaceSecondaryBrush");

        var kpiGrid = new Grid();
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        kpiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        kpiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        kpiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddKpi(string title, TextBlock val, int row, int col)
        {
            var sp = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };
            var t = PimUi.Text(title, 11, brush: "TextSecondaryBrush");
            sp.Children.Add(t);
            sp.Children.Add(val);
            Grid.SetRow(sp, row);
            Grid.SetColumn(sp, col);
            kpiGrid.Children.Add(sp);
        }

        AddKpi(L.T("Toplam"), _kpiTotal, 0, 0);
        AddKpi(L.T("Aktif"), _kpiActive, 0, 1);
        AddKpi(L.T("Geciken"), _kpiOverdue, 1, 0);
        AddKpi(L.T("Kazanılan"), _kpiWon, 1, 1);
        kpiBorder.Child = kpiGrid;
        side.Children.Add(kpiBorder);

        SidePanel = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UiTranslator.SetSkip(SidePanel, true);

        // 3. Main Table (GridView)
        SetupGrid();

        var root = new Grid();
        root.Children.Add(_list);

        _emptyHint.Text = L.T("Kayıtlı teklif bulunamadı.");
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyHint.VerticalAlignment = VerticalAlignment.Center;
        _emptyHint.Visibility = Visibility.Collapsed;
        root.Children.Add(_emptyHint);

        Content = root;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        // Event hooks
        _list.MouseDoubleClick += (_, _) => OnEditSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { OnDeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.Enter) { OnEditSelected(); e.Handled = true; }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { OnNewQuote(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
        };

        _store.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Loaded += (_, _) => Refresh();
    }

    void SetupGrid()
    {
        var gv = new GridView();

        // 1. Status Column
        var colStatus = new GridViewColumn { Header = L.T("Durum"), Width = 110 };
        var statusFactory = new FrameworkElementFactory(typeof(Border));
        statusFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        statusFactory.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
        statusFactory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        statusFactory.SetBinding(Border.BackgroundProperty, new Binding("StatusColor"));

        var statusText = new FrameworkElementFactory(typeof(TextBlock));
        statusText.SetBinding(TextBlock.TextProperty, new Binding("Item.StatusDisplayName"));
        statusText.SetValue(TextBlock.FontSizeProperty, 11.5);
        statusText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        statusText.SetBinding(TextBlock.ForegroundProperty, new Binding("StatusTextColor"));
        statusFactory.AppendChild(statusText);

        colStatus.CellTemplate = new DataTemplate { VisualTree = statusFactory };
        gv.Columns.Add(colStatus);

        // 2. Part Number Column
        var colPart = new GridViewColumn { Header = L.T("Parça No"), Width = 130 };
        var partFactory = new FrameworkElementFactory(typeof(TextBlock));
        partFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.PartNumber"));
        partFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        partFactory.SetValue(TextBlock.FontSizeProperty, 12.5);
        partFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        colPart.CellTemplate = new DataTemplate { VisualTree = partFactory };
        gv.Columns.Add(colPart);

        // 3. Revision Column
        var colRev = new GridViewColumn { Header = L.T("Rev"), Width = 60 };
        var revFactory = new FrameworkElementFactory(typeof(TextBlock));
        revFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.Revision"));
        revFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        revFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        colRev.CellTemplate = new DataTemplate { VisualTree = revFactory };
        gv.Columns.Add(colRev);

        // 4. Quantity Column
        var colQty = new GridViewColumn { Header = L.T("Adet"), Width = 90 };
        var qtyFactory = new FrameworkElementFactory(typeof(TextBlock));
        qtyFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.QuantityDisplay"));
        qtyFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        qtyFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        colQty.CellTemplate = new DataTemplate { VisualTree = qtyFactory };
        gv.Columns.Add(colQty);

        // 5. Material Column
        var colMat = new GridViewColumn { Header = L.T("Malzeme"), Width = 130 };
        var matFactory = new FrameworkElementFactory(typeof(TextBlock));
        matFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.Material"));
        matFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        matFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        colMat.CellTemplate = new DataTemplate { VisualTree = matFactory };
        gv.Columns.Add(colMat);

        // 6. Customer Column
        var colCust = new GridViewColumn { Header = L.T("Müşteri"), Width = 140 };
        var custFactory = new FrameworkElementFactory(typeof(TextBlock));
        custFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.Customer"));
        custFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Medium);
        custFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        custFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        colCust.CellTemplate = new DataTemplate { VisualTree = custFactory };
        gv.Columns.Add(colCust);

        // 7. Contact Column
        var colContact = new GridViewColumn { Header = L.T("İlgili Kişi"), Width = 130 };
        var contactFactory = new FrameworkElementFactory(typeof(TextBlock));
        contactFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.Contact"));
        contactFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        contactFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        colContact.CellTemplate = new DataTemplate { VisualTree = contactFactory };
        gv.Columns.Add(colContact);

        // 8. Quote Deadline Column
        var colDeadline = new GridViewColumn { Header = L.T("Son Teklif Tarihi"), Width = 130 };
        var deadFactory = new FrameworkElementFactory(typeof(TextBlock));
        deadFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.DeadlineDisplay"));
        deadFactory.SetBinding(TextBlock.ForegroundProperty, new Binding("DeadColor"));
        deadFactory.SetBinding(TextBlock.FontWeightProperty, new Binding("DeadFontWeight"));
        colDeadline.CellTemplate = new DataTemplate { VisualTree = deadFactory };
        gv.Columns.Add(colDeadline);

        // 9. Requested Delivery Column
        var colDeliv = new GridViewColumn { Header = L.T("İstenen Teslim"), Width = 120 };
        var delivFactory = new FrameworkElementFactory(typeof(TextBlock));
        delivFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.RequestedDeliveryDisplay"));
        delivFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        colDeliv.CellTemplate = new DataTemplate { VisualTree = delivFactory };
        gv.Columns.Add(colDeliv);

        // 10. Notes Column
        var colNotes = new GridViewColumn { Header = L.T("Notlar"), Width = 180 };
        var notesFactory = new FrameworkElementFactory(typeof(TextBlock));
        notesFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.Notes"));
        notesFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        notesFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        colNotes.CellTemplate = new DataTemplate { VisualTree = notesFactory };
        gv.Columns.Add(colNotes);

        // 11. Source Mail Column
        var colMail = new GridViewColumn { Header = L.T("Kaynak İleti"), Width = 160 };
        var mailFactory = new FrameworkElementFactory(typeof(TextBlock));
        mailFactory.SetBinding(TextBlock.TextProperty, new Binding("Item.SourceSubject"));
        mailFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        mailFactory.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        colMail.CellTemplate = new DataTemplate { VisualTree = mailFactory };
        gv.Columns.Add(colMail);

        _list.View = gv;

        // Context Menu for rows
        var menu = new ContextMenu();
        void AddMenuItem(string text, string glyph, Action action)
        {
            var mi = new MenuItem { Header = text, Icon = glyph };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        AddMenuItem(L.T("Düzenle..."), "", OnEditSelected);
        AddMenuItem(L.T("Teklif Yanıtı Yaz..."), "", OnReplyQuote);
        AddMenuItem(L.T("Kaynak Maili Aç"), "", OnOpenSourceMail);
        menu.Items.Add(new Separator());

        var statusSub = new MenuItem { Header = L.T("Durumu Değiştir"), Icon = "" };
        foreach (QuoteStatus st in Enum.GetValues(typeof(QuoteStatus)))
        {
            var stVal = st;
            var stItem = new MenuItem { Header = stVal switch
            {
                QuoteStatus.New => L.T("Yeni"),
                QuoteStatus.InProgress => L.T("Hazırlanıyor"),
                QuoteStatus.Sent => L.T("Gönderildi"),
                QuoteStatus.Won => L.T("Kazanıldı"),
                QuoteStatus.Lost => L.T("Kaybedildi"),
                _ => stVal.ToString()
            }};
            stItem.Click += (_, _) => SetSelectedStatus(stVal);
            statusSub.Items.Add(stItem);
        }
        menu.Items.Add(statusSub);

        menu.Items.Add(new Separator());
        AddMenuItem(L.T("Excel'e Aktar"), "", OnExportExcel);
        AddMenuItem(L.T("Sil"), "", OnDeleteSelected);

        _list.ContextMenu = menu;
        _list.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));
    }

    void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader header && header.Column?.Header is string headerText)
        {
            var prop = headerText switch
            {
                var h when h == L.T("Durum") => "Status",
                var h when h == L.T("Parça No") => "PartNumber",
                var h when h == L.T("Rev") => "Revision",
                var h when h == L.T("Adet") => "Quantity",
                var h when h == L.T("Malzeme") => "Material",
                var h when h == L.T("Müşteri") => "Customer",
                var h when h == L.T("İlgili Kişi") => "Contact",
                var h when h == L.T("Son Teklif Tarihi") => "Deadline",
                var h when h == L.T("İstenen Teslim") => "RequestedDelivery",
                var h when h == L.T("Kaynak İleti") => "SourceSubject",
                _ => "CreatedAt"
            };

            if (_sortColumn == prop)
                _sortAsc = !_sortAsc;
            else
            {
                _sortColumn = prop;
                _sortAsc = true;
            }
            Refresh();
        }
    }

    public void Refresh()
    {
        _store.ReloadIfChanged();
        var selectedId = (_list.SelectedItem as QuoteRowVm)?.Item.Id;
        var quotes = _store.Quotes;
        var qry = _search.Text.Trim().ToLowerInvariant();

        // Update KPIs
        var total = quotes.Count;
        var active = quotes.Count(q => q.Status is QuoteStatus.New or QuoteStatus.InProgress);
        var overdue = quotes.Count(q => q.IsDeadlineOverdue);
        var won = quotes.Count(q => q.Status == QuoteStatus.Won);

        _kpiTotal.Text = total.ToString();
        _kpiActive.Text = active.ToString();
        _kpiOverdue.Text = overdue.ToString();
        _kpiOverdue.Foreground = overdue > 0 ? Brushes.Crimson : (Brush)FindResource("TextPrimaryBrush");
        _kpiWon.Text = won.ToString();
        _kpiWon.Foreground = won > 0 ? Brushes.ForestGreen : (Brush)FindResource("TextPrimaryBrush");

        // Filter list
        IEnumerable<QuoteItem> filtered = quotes;
        filtered = _filter switch
        {
            QuoteFilter.Active => filtered.Where(q => q.Status is QuoteStatus.New or QuoteStatus.InProgress),
            QuoteFilter.New => filtered.Where(q => q.Status == QuoteStatus.New),
            QuoteFilter.InProgress => filtered.Where(q => q.Status == QuoteStatus.InProgress),
            QuoteFilter.Sent => filtered.Where(q => q.Status == QuoteStatus.Sent),
            QuoteFilter.Won => filtered.Where(q => q.Status == QuoteStatus.Won),
            QuoteFilter.Lost => filtered.Where(q => q.Status == QuoteStatus.Lost),
            QuoteFilter.Overdue => filtered.Where(q => q.IsDeadlineOverdue),
            _ => filtered
        };

        if (!string.IsNullOrEmpty(qry))
        {
            filtered = filtered.Where(q =>
                (q.PartNumber ?? "").ToLowerInvariant().Contains(qry) ||
                (q.Customer ?? "").ToLowerInvariant().Contains(qry) ||
                (q.Contact ?? "").ToLowerInvariant().Contains(qry) ||
                (q.Material ?? "").ToLowerInvariant().Contains(qry) ||
                (q.Notes ?? "").ToLowerInvariant().Contains(qry) ||
                (q.SourceSubject ?? "").ToLowerInvariant().Contains(qry));
        }

        // Sorting
        filtered = (_sortColumn, _sortAsc) switch
        {
            ("Status", true) => filtered.OrderBy(q => q.Status),
            ("Status", false) => filtered.OrderByDescending(q => q.Status),
            ("PartNumber", true) => filtered.OrderBy(q => q.PartNumber),
            ("PartNumber", false) => filtered.OrderByDescending(q => q.PartNumber),
            ("Revision", true) => filtered.OrderBy(q => q.Revision),
            ("Revision", false) => filtered.OrderByDescending(q => q.Revision),
            ("Quantity", true) => filtered.OrderBy(q => q.Quantity),
            ("Quantity", false) => filtered.OrderByDescending(q => q.Quantity),
            ("Material", true) => filtered.OrderBy(q => q.Material),
            ("Material", false) => filtered.OrderByDescending(q => q.Material),
            ("Customer", true) => filtered.OrderBy(q => q.Customer),
            ("Customer", false) => filtered.OrderByDescending(q => q.Customer),
            ("Contact", true) => filtered.OrderBy(q => q.Contact),
            ("Contact", false) => filtered.OrderByDescending(q => q.Contact),
            ("Deadline", true) => filtered.OrderBy(q => q.Deadline ?? DateTime.MaxValue),
            ("Deadline", false) => filtered.OrderByDescending(q => q.Deadline ?? DateTime.MinValue),
            ("RequestedDelivery", true) => filtered.OrderBy(q => q.RequestedDelivery ?? DateTime.MaxValue),
            ("RequestedDelivery", false) => filtered.OrderByDescending(q => q.RequestedDelivery ?? DateTime.MinValue),
            ("SourceSubject", true) => filtered.OrderBy(q => q.SourceSubject ?? ""),
            ("SourceSubject", false) => filtered.OrderByDescending(q => q.SourceSubject ?? ""),
            (_, true) => filtered.OrderBy(q => q.CreatedAt),
            _ => filtered.OrderByDescending(q => q.CreatedAt)
        };

        _currentView = filtered.ToList();

        // Map to view models
        var rows = _currentView.Select(q => new QuoteRowVm(q)).ToList();
        _list.ItemsSource = rows;
        if (selectedId != null) _list.SelectedItem = rows.FirstOrDefault(r => r.Item.Id == selectedId);

        _emptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Highlight active filter button
        foreach (var child in _filterButtons.Children)
        {
            if (child is Button b && b.DataContext is QuoteFilter qf)
            {
                if (qf == _filter)
                    b.SetResourceReference(ForegroundProperty, "AccentPrimaryBrush");
                else
                    b.ClearValue(ForegroundProperty);
            }
        }
    }

    QuoteItem? SelectedQuote => (_list.SelectedItem as QuoteRowVm)?.Item;

    void OnNewQuote()
    {
        var item = new QuoteItem { Deadline = DateTime.Today.AddDays(3) };
        var dlg = new QuoteEditDialog(item, isNew: true) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            _store.AddOrUpdate(dlg.Item);
            _host.Status(L.F("Yeni teklif eklendi: {0}", dlg.Item.PartNumber));
            Refresh();
        }
    }

    void OnEditSelected()
    {
        var q = SelectedQuote;
        if (q == null) return;
        var dlg = new QuoteEditDialog(q, isNew: false) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            _store.AddOrUpdate(dlg.Item);
            _host.Status(L.F("Teklif güncellendi: {0}", dlg.Item.PartNumber));
            Refresh();
        }
    }

    void OnDeleteSelected()
    {
        var q = SelectedQuote;
        if (q == null) return;
        if (MessageBox.Show(Window.GetWindow(this),
            L.F("{0} ({1}) teklif kaydı silinsin mi?", q.PartNumber, q.Customer),
            L.T("Teklifi Sil"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _store.Delete(q.Id);
            _host.Status(L.F("Teklif silindi: {0}", q.PartNumber));
            Refresh();
        }
    }

    void SetSelectedStatus(QuoteStatus status)
    {
        var q = SelectedQuote;
        if (q == null) return;
        q.Status = status;
        _store.AddOrUpdate(q);
        _host.Status(L.F("Teklif durumu güncellendi: {0} -> {1}", q.PartNumber, q.StatusDisplayName));
        Refresh();
    }

    void OnExportExcel()
    {
        var sfd = new SaveFileDialog
        {
            Filter = "Excel Çalışma Kitabı (*.xlsx)|*.xlsx",
            FileName = $"MailStudio_Teklif_Masasi_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
        };
        if (sfd.ShowDialog(Window.GetWindow(this)) == true)
        {
            try
            {
                QuoteStore.ExportToExcel(_currentView.Count > 0 ? _currentView : _store.Quotes, sfd.FileName);
                _host.Status(L.T("Teklifler Excel dosyasına aktarıldı."));
                if (MessageBox.Show(Window.GetWindow(this),
                    L.T("Dosya başarıyla oluşturuldu. Şimdi açmak ister misiniz?"),
                    L.T("Excel'e Aktar"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), L.T("Dışa aktarma hatası: ") + ex.Message, L.T("Hata"));
            }
        }
    }

    void OnReplyQuote()
    {
        var q = SelectedQuote;
        if (q == null) return;
        _host.ComposeQuoteReply(q);
    }

    void OnOpenSourceMail()
    {
        var q = SelectedQuote;
        if (q == null || string.IsNullOrEmpty(q.SourceMail))
        {
            _host.Status(L.T("Bu teklifin bağlı olduğu bir kaynak mail bulunmuyor."));
            return;
        }
        _host.OpenSourceMail(q.SourceMail);
    }
}

public sealed class QuoteRowVm
{
    public QuoteItem Item { get; }

    public QuoteRowVm(QuoteItem item)
    {
        Item = item;
    }

    public Brush StatusColor => Item.Status switch
    {
        QuoteStatus.New => new SolidColorBrush(Color.FromArgb(40, 15, 108, 189)),        // Blue tint
        QuoteStatus.InProgress => new SolidColorBrush(Color.FromArgb(40, 247, 99, 12)),  // Orange tint
        QuoteStatus.Sent => new SolidColorBrush(Color.FromArgb(40, 0, 150, 136)),        // Teal tint
        QuoteStatus.Won => new SolidColorBrush(Color.FromArgb(40, 16, 124, 16)),         // Green tint
        QuoteStatus.Lost => new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),      // Gray tint
        _ => Brushes.Transparent
    };

    public Brush StatusTextColor => Item.Status switch
    {
        QuoteStatus.New => new SolidColorBrush(Color.FromRgb(15, 108, 189)),
        QuoteStatus.InProgress => new SolidColorBrush(Color.FromRgb(215, 80, 5)),
        QuoteStatus.Sent => new SolidColorBrush(Color.FromRgb(0, 121, 107)),
        QuoteStatus.Won => new SolidColorBrush(Color.FromRgb(16, 124, 16)),
        QuoteStatus.Lost => Brushes.Gray,
        _ => Brushes.Black
    };

    public Brush DeadColor
    {
        get
        {
            if (Item.IsDeadlineOverdue) return Brushes.Crimson;
            if (Item.IsDeadlineToday) return Brushes.DarkOrange;
            return Brushes.Gray;
        }
    }

    public FontWeight DeadFontWeight =>
        Item.IsDeadlineOverdue || Item.IsDeadlineToday ? FontWeights.Bold : FontWeights.Normal;
}

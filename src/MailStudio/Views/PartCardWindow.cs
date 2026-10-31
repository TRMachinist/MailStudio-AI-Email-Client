using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Calendar;
using MailStudio.Views.Calendar;

namespace MailStudio.Views;

/// <summary>What the part card needs from the main window.</summary>
public interface IPartCardHost
{
    void OpenMail(MailHeader mail);
    Task OpenAttachmentAsync(MailHeader mail, AttachmentInfo attachment);
    Task CompareFilesAsync(PartFile older, PartFile newer);
    void OpenQuote(QuoteItem quote);
    Task PreviewNcAsync(PartFile file);
}

/// <summary>
/// Everything about one part number on one page: every mail that mentions it (timeline), the revisions with their
/// drawings / models (open, compare with the previous revision), the quotes, the NC programs and the tasks.
/// </summary>
public sealed class PartCardWindow : Window
{
    readonly MailCache _cache; readonly QuoteStore _quotes; readonly TaskStore _tasks; readonly IPartCardHost _host;
    readonly TextBox _query = new() { Height = 30, Width = 220, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _title = PimUi.Text("", 22, bold: true);
    readonly WrapPanel _facts = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TabControl _tabs = new() { Margin = new Thickness(16, 8, 16, 0) };
    readonly TextBlock _empty = PimUi.Text("", 13, brush: "TextMutedBrush");

    public PartHistory? History { get; private set; }

    public PartCardWindow(string partNumber, MailCache cache, QuoteStore quotes, TaskStore tasks, IPartCardHost host)
    {
        _cache = cache; _quotes = quotes; _tasks = tasks; _host = host;
        UiTranslator.SetSkip(this, true);
        Width = 1080; Height = 760; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var top = new DockPanel { Margin = new Thickness(16, 14, 16, 0) };
        var search = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _query.ToolTip = L.T("Başka bir parça numarası yazıp Enter'a basın");
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) Load(_query.Text); };
        var go = new Button { Content = L.T("Göster"), Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 0, 0) };
        go.SetResourceReference(StyleProperty, "RibbonCmd"); go.Click += (_, _) => Load(_query.Text);
        search.Children.Add(_query); search.Children.Add(go);
        DockPanel.SetDock(search, Dock.Right); top.Children.Add(search);
        var head = new StackPanel();
        head.Children.Add(PimUi.Text(L.T("PARÇA KARTI"), 11, bold: true, brush: "AccentPrimaryBrush"));
        head.Children.Add(_title); head.Children.Add(_facts);
        top.Children.Add(head);

        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 10, 16, 12) };
        var close = new Button { Content = L.T("Kapat"), MinWidth = 90, Height = 30, IsCancel = true };
        close.SetResourceReference(StyleProperty, "RibbonCmd"); close.Click += (_, _) => Close();
        foot.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        var body = new Grid(); body.Children.Add(_tabs);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; _empty.TextWrapping = TextWrapping.Wrap; _empty.Margin = new Thickness(40);
        body.Children.Add(_empty);
        root.Children.Add(body);
        Content = root;
        Load(partNumber);
    }

    public void Load(string part)
    {
        part = part.Trim();
        if (part.Length == 0) return;
        _query.Text = part;
        Title = L.T("Parça kartı") + " – " + part;
        _title.Text = part;
        _tabs.Items.Clear(); _facts.Children.Clear();
        PartHistory h;
        try { h = PartHistory.Build(part, _cache, _quotes, _tasks); }
        catch (Exception ex) { _empty.Text = ex.Message; _empty.Visibility = Visibility.Visible; _tabs.Visibility = Visibility.Collapsed; return; }
        History = h;
        if (h.Mails.Count == 0 && h.Quotes.Count == 0 && h.Tasks.Count == 0)
        {
            _empty.Text = L.F("\"{0}\" hiçbir mailde, teklifte veya görevde geçmiyor (bu bilgisayardaki posta kopyasında).", part);
            _empty.Visibility = Visibility.Visible; _tabs.Visibility = Visibility.Collapsed; return;
        }
        _empty.Visibility = Visibility.Collapsed; _tabs.Visibility = Visibility.Visible;

        if (h.LatestRevision.Length > 0) Fact(L.F("Son revizyon: {0}", h.LatestRevision), true);
        if (h.FirstSeen is { } f && h.LastSeen is { } l) Fact($"{f.LocalDateTime.ToString("d", L.Culture)} – {l.LocalDateTime.ToString("d", L.Culture)}");
        Fact(L.F("{0} mail", h.Mails.Count)); Fact(L.F("{0} dosya", h.Files.Count));
        if (h.Quotes.Count > 0)
        {
            var last = h.Quotes[^1];
            Fact(L.F("Teklif: {0}", last.StatusDisplayName) + (last.UnitPrice is { } p ? " · " + Services.Costing.CostEstimator.Money(p, last.Currency ?? "EUR") : ""), last.Status == QuoteStatus.Won);
        }
        if (h.Customers.Count > 0) Fact(string.Join(", ", h.Customers));

        _tabs.Items.Add(new TabItem { Header = L.F("Zaman çizelgesi ({0})", h.Mails.Count), Content = Timeline(h) });
        _tabs.Items.Add(new TabItem { Header = L.F("Revizyonlar ({0})", h.Revisions.Count), Content = RevisionsView(h) });
        _tabs.Items.Add(new TabItem { Header = L.F("Dosyalar ({0})", h.Files.Count), Content = FilesView(h.Files.OrderByDescending(x => x.Date)) });
        _tabs.Items.Add(new TabItem { Header = L.F("Teklifler ({0})", h.Quotes.Count), Content = QuotesView(h) });
        _tabs.Items.Add(new TabItem { Header = L.F("NC programları ({0})", h.NcPrograms.Count()), Content = FilesView(h.NcPrograms.OrderByDescending(x => x.Date), nc: true) });
        _tabs.Items.Add(new TabItem { Header = L.F("Görevler ({0})", h.Tasks.Count), Content = TasksView(h) });
        _tabs.SelectedIndex = 0;
    }

    void Fact(string text, bool accent = false)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 4) };
        b.SetResourceReference(Border.BackgroundProperty, accent ? "AccentSubtleBrush" : "BgSurfaceSecondaryBrush");
        b.Child = PimUi.Text(text, 12, bold: accent, brush: accent ? "AccentPrimaryBrush" : "TextSecondaryBrush");
        _facts.Children.Add(b);
    }

    static ListView List(params (string Header, string Path, double Width)[] cols)
    {
        var view = new GridView();
        foreach (var (header, path, width) in cols) view.Columns.Add(new GridViewColumn { Header = header, DisplayMemberBinding = new Binding(path), Width = width });
        return new ListView { View = view, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) };
    }

    FrameworkElement Timeline(PartHistory h)
    {
        var list = List((L.T("Tarih"), "Date", 130), (L.T("Kimden"), "From", 190), (L.T("Konu"), "Subject", 420), (L.T("Ekler"), "Files", 220));
        list.ItemsSource = h.Mails.OrderByDescending(m => m.Date).Select(m => new
        {
            Mail = m, Date = m.Date.LocalDateTime.ToString("g", L.Culture), From = string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : m.FromName, m.Subject,
            Files = string.Join(", ", h.Files.Where(f => f.Mail == m).Select(f => f.FileName))
        }).ToList();
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is { } o && o.GetType().GetProperty("Mail")?.GetValue(o) is MailHeader m) _host.OpenMail(m); };
        list.ToolTip = L.T("Çift tık: maili aç");
        return list;
    }

    FrameworkElement RevisionsView(PartHistory h)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };
        if (h.Revisions.Count == 0)
        {
            panel.Children.Add(PimUi.Text(L.T("Dosya adlarında ve mail konularında revizyon bilgisi bulunamadı."), 12.5, brush: "TextMutedBrush"));
            return new ScrollViewer { Content = panel };
        }
        PartRevision? previous = null;
        foreach (var rev in h.Revisions)
        {
            var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
            card.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            var sp = new StackPanel();
            var isLatest = rev == h.Revisions[^1];
            sp.Children.Add(PimUi.Text(L.F("Rev {0}", rev.Revision) + (isLatest ? "  · " + L.T("güncel") : ""), 15, bold: true, brush: isLatest ? "AccentPrimaryBrush" : "TextPrimaryBrush"));
            sp.Children.Add(PimUi.Text(L.F("İlk görüldüğü: {0:g}", rev.FirstSeen.LocalDateTime), 11.5, brush: "TextSecondaryBrush"));
            foreach (var file in rev.Files)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(SmallButton(L.T("Aç"), async () => await _host.OpenAttachmentAsync(file.Mail, file.Attachment)));
                var older = previous?.Files.FirstOrDefault(o => SameFamily(o, file));
                if (older != null) buttons.Children.Add(SmallButton(L.F("Rev {0} ile karşılaştır", previous!.Revision), async () => await _host.CompareFilesAsync(older, file)));
                DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
                row.Children.Add(PimUi.Text($"{KindText(file.Kind)} · {file.FileName}   ({file.Attachment.SizeDisplay})", 12.5));
                sp.Children.Add(row);
            }
            card.Child = sp; panel.Children.Insert(0, card);   // newest on top
            previous = rev;
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static bool SameFamily(PartFile a, PartFile b) =>
        a.Kind == b.Kind && a.Kind is "drawing" or "cad2d" or "model" or "sheet"
        && string.Equals(System.IO.Path.GetExtension(a.FileName), System.IO.Path.GetExtension(b.FileName), StringComparison.OrdinalIgnoreCase)
        && Stem(a) == Stem(b);

    static string Stem(PartFile f) => RevisionService.PartKey(RevisionService.ParsePartAndRevision("", f.FileName).PartNumber);

    static string KindText(string kind) => kind switch
    {
        "drawing" => L.T("Teknik resim"), "cad2d" => L.T("2B CAD"), "model" => L.T("3B model"), "sheet" => L.T("Tablo"),
        "archive" => L.T("Arşiv"), "nc" => L.T("NC programı"), _ => L.T("Dosya")
    };

    Button SmallButton(string text, Func<Task> click)
    {
        var b = new Button { Content = text, Height = 26, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0) };
        b.SetResourceReference(StyleProperty, "RibbonCmd");
        b.Click += async (_, _) => { b.IsEnabled = false; try { await click(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, L.ProductName); } finally { b.IsEnabled = true; } };
        return b;
    }

    FrameworkElement FilesView(IEnumerable<PartFile> files, bool nc = false)
    {
        var list = List((L.T("Tarih"), "Date", 130), (L.T("Tür"), "Kind", 110), (L.T("Dosya"), "Name", 300), ("Rev", "Rev", 50), (L.T("Mail"), "Subject", 330));
        var rows = files.Select(f => new { File = f, Date = f.Date.LocalDateTime.ToString("g", L.Culture), Kind = KindText(f.Kind), Name = f.FileName, Rev = f.Revision, f.Mail.Subject }).ToList();
        list.ItemsSource = rows;
        list.ToolTip = nc ? L.T("Çift tık: takım yolu önizlemesi") : L.T("Çift tık: dosyayı aç");
        list.MouseDoubleClick += async (_, _) =>
        {
            if (list.SelectedItem?.GetType().GetProperty("File")?.GetValue(list.SelectedItem) is not PartFile f) return;
            try { if (f.Kind == "nc") await _host.PreviewNcAsync(f); else await _host.OpenAttachmentAsync(f.Mail, f.Attachment); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.ProductName); }
        };
        if (rows.Count == 0)
            return new TextBlock { Text = nc ? L.T("Bu parçaya ait NC programı (.nc, .tap, .mpf, .h…) maillerde yok.") : L.T("Dosya yok."), Margin = new Thickness(8, 12, 8, 8), Foreground = ThemeManager.Brush("TextMutedBrush") };
        return list;
    }

    FrameworkElement QuotesView(PartHistory h)
    {
        if (h.Quotes.Count == 0) return new TextBlock { Text = L.T("Bu parça için teklif kaydı yok."), Margin = new Thickness(8, 12, 8, 8), Foreground = ThemeManager.Brush("TextMutedBrush") };
        var list = List((L.T("Durum"), "Status", 110), ("Rev", "Rev", 50), (L.T("Adet"), "Qty", 60), (L.T("Birim fiyat"), "Price", 110), (L.T("Müşteri"), "Customer", 200), (L.T("Oluşturma"), "Created", 130), (L.T("Teklif no"), "Number", 110));
        list.ItemsSource = h.Quotes.OrderByDescending(q => q.CreatedAt).Select(q => new
        {
            Quote = q, Status = q.StatusDisplayName, Rev = q.Revision, Qty = q.Quantity, Price = q.PriceDisplay, q.Customer,
            Created = q.CreatedAt.LocalDateTime.ToString("d", L.Culture), Number = q.QuoteNumber ?? ""
        }).ToList();
        list.ToolTip = L.T("Çift tık: Teklif Masası'nda göster");
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem?.GetType().GetProperty("Quote")?.GetValue(list.SelectedItem) is QuoteItem q) { _host.OpenQuote(q); } };
        return list;
    }

    FrameworkElement TasksView(PartHistory h)
    {
        if (h.Tasks.Count == 0) return new TextBlock { Text = L.T("Bu parçayı anan görev yok."), Margin = new Thickness(8, 12, 8, 8), Foreground = ThemeManager.Brush("TextMutedBrush") };
        var list = List((L.T("Görev"), "Title", 420), (L.T("Son tarih"), "Due", 120), (L.T("Durum"), "State", 110));
        list.ItemsSource = h.Tasks.Select(t => new { t.Title, Due = t.DueDate?.ToString("d", L.Culture) ?? "", State = t.Completed ? L.T("Tamamlandı") : L.T("Açık") }).ToList();
        return list;
    }
}

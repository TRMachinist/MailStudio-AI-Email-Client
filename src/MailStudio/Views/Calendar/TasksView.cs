using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Calendar;

namespace MailStudio.Views.Calendar;

/// <summary>Mail-side services the task list uses (flagged mails are tasks too, like Outlook's To-Do bar).</summary>
public interface ITaskHost : IPimHost
{
    IReadOnlyList<MailHeader> FlaggedMails();
    void SetMailFlag(MailHeader mail, bool flagged);
    void OpenMail(MailHeader mail);
}

public enum TaskFilter { Active, Today, Week, Overdue, Completed, All }

/// <summary>Outlook "Tasks / To-Do": quick add, due-date groups, flagged mails, details pane, reminders.</summary>
public sealed class TasksView : UserControl
{
    readonly TaskStore _store;
    readonly ITaskHost _host;
    readonly ListBox _list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _quick = new() { Margin = new Thickness(12, 10, 12, 6), Padding = new Thickness(8, 4, 8, 4) };
    readonly TextBlock _quickHint = PimUi.Text("", 12.5, brush: "TextMutedBrush");
    readonly CheckBox _showFlagged = new() { Content = L.T("Bayraklı mailleri göster"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    readonly StackPanel _filters = new();
    readonly Grid _detail = new() { Margin = new Thickness(16, 12, 16, 12) };
    static TaskFilter _lastFilter = TaskFilter.Active;
    TaskFilter _filter = _lastFilter;
    TaskItem? _editing;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool _loadingDetail;

    public WrapPanel Ribbon { get; } = new() { Orientation = Orientation.Horizontal };
    public FrameworkElement SidePanel { get; }

    public TasksView(TaskStore store, ITaskHost host)
    {
        _store = store; _host = host;
        UiTranslator.SetSkip(this, true); UiTranslator.SetSkip(Ribbon, true);
        Ribbon.Children.Add(PimUi.Group(L.T("Yeni"), PimUi.Cmd("", L.T("Yeni Görev"), (_, _) => { _quick.Focus(); }, L.T("Yeni görev (Ctrl+N)"), primary: true)));
        Ribbon.Children.Add(PimUi.Group(L.T("Yönet"),
            PimUi.Cmd("", L.T("Tamamlandı"), (_, _) => ToggleSelected(), L.T("Tamamlandı olarak işaretle (Ctrl+Enter)")),
            PimUi.Cmd("", L.T("Sil"), (_, _) => DeleteSelected(), L.T("Sil (Delete)"))));
        Ribbon.Children.Add(PimUi.Group(L.T("Görünüm"), _showFlagged));
        _showFlagged.Checked += (_, _) => Refresh(); _showFlagged.Unchecked += (_, _) => Refresh();

        foreach (var (f, text, glyph) in new[] { (TaskFilter.Active, L.T("Etkin görevler"), ""), (TaskFilter.Today, L.T("Bugün"), ""), (TaskFilter.Week, L.T("Bu hafta"), ""),
                     (TaskFilter.Overdue, L.T("Gecikmiş"), ""), (TaskFilter.Completed, L.T("Tamamlanan"), ""), (TaskFilter.All, L.T("Tümü"), "") })
        {
            var b = PimUi.Cmd(glyph, text, (_, _) => { _filter = _lastFilter = f; Refresh(); });
            b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(6, 1, 6, 1); b.Tag = glyph; b.DataContext = f;
            _filters.Children.Add(b);
        }
        var sideTitle = PimUi.Text(L.T("Yapılacaklar"), 13, bold: true); sideTitle.Margin = new Thickness(12, 10, 8, 6);
        var side = new StackPanel(); side.Children.Add(sideTitle); side.Children.Add(_filters);
        SidePanel = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UiTranslator.SetSkip(SidePanel, true);

        var root = new Grid();
        root.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star), MinWidth = 320 });
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });
        var left = new DockPanel();
        var quickHost = new Grid(); quickHost.Children.Add(_quick);
        _quickHint.Text = L.T("+ Görev ekleyin ve Enter'a basın (ör. \"Teklifi gönder yarın\")");
        _quickHint.IsHitTestVisible = false; _quickHint.Margin = new Thickness(22, 14, 12, 6); _quickHint.VerticalAlignment = VerticalAlignment.Top;
        quickHost.Children.Add(_quickHint);
        _quick.TextChanged += (_, _) => _quickHint.Visibility = _quick.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _quick.KeyDown += (_, e) => { if (e.Key == Key.Enter && _quick.Text.Trim().Length > 0) { QuickAdd(_quick.Text.Trim()); _quick.Clear(); e.Handled = true; } };
        DockPanel.SetDock(quickHost, Dock.Top); left.Children.Add(quickHost);
        left.Children.Add(_list);
        root.Children.Add(left);
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        split.SetResourceReference(BackgroundProperty, "BorderSubtleBrush");
        Grid.SetColumn(split, 1); root.Children.Add(split);
        var detailScroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(detailScroll, 2); root.Children.Add(detailScroll);
        Content = root;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        _list.SelectionChanged += (_, _) => ShowDetail(_list.SelectedItem is ListBoxItem { Tag: TaskItem t } ? t : null, _list.SelectedItem is ListBoxItem { Tag: MailHeader m } ? m : null);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: MailHeader m }) _host.OpenMail(m); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { ToggleSelected(); e.Handled = true; }
            else if (e.Key == Key.Space) { ToggleSelected(); e.Handled = true; }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { _quick.Focus(); e.Handled = true; } };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveDetail(); };
        _store.Changed += () => Dispatcher.BeginInvoke(() => { if (_ownSaves > 0) { _ownSaves--; return; } Refresh(); });
        Loaded += (_, _) => { Refresh(); _quick.Focus(); };
        Unloaded += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveDetail(); } };
    }

    int _ownSaves;

    /// <summary>"Teklifi gönder yarın" → due tomorrow; "… bugün", "… cuma", "… 25.10" are understood too.</summary>
    void QuickAdd(string text)
    {
        var (title, due) = ParseQuick(text);
        var t = new TaskItem { Title = title, DueDate = due };
        _store.Save(t);
        Refresh(select: t.Id);
        _host.Status(L.F("Görev eklendi: {0}", title));
    }

    public static (string Title, DateTime? Due) ParseQuick(string text, DateTime? today = null)
    {
        var day = (today ?? DateTime.Today).Date;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count < 2) return (text, null);
        var last = words[^1].ToLower(L.Culture).Trim('.', ',');
        DateTime? due = null;
        string[] todayWords = { L.T("bugün").ToLower(L.Culture), "today", "bugün" }, tomorrowWords = { L.T("yarın").ToLower(L.Culture), "tomorrow", "yarın" };
        if (todayWords.Contains(last)) due = day;
        else if (tomorrowWords.Contains(last)) due = day.AddDays(1);
        else
        {
            for (var i = 0; i < 7; i++)
            {
                var d = (DayOfWeek)i;
                if (last == L.Culture.DateTimeFormat.GetDayName(d).ToLower(L.Culture) || last == System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(d).ToLowerInvariant())
                { var delta = ((int)d - (int)day.DayOfWeek + 7) % 7; due = day.AddDays(delta == 0 ? 7 : delta); break; }
            }
            if (due == null && System.Text.RegularExpressions.Regex.IsMatch(last, @"^\d{1,2}[./]\d{1,2}([./]\d{2,4})?$") &&
                DateTime.TryParse(last.Replace('/', '.').Split('.').Length == 2 ? last.Replace('/', '.') + "." + day.Year : last.Replace('/', '.'), L.Culture, System.Globalization.DateTimeStyles.None, out var parsed))
                due = parsed.Date < day.AddMonths(-1) ? parsed.AddYears(1).Date : parsed.Date;
        }
        if (due == null) return (text, null);
        return (string.Join(' ', words.Take(words.Count - 1)), due);
    }

    // ===================================================================== list

    public void Refresh() => Refresh(null);

    void Refresh(string? select)
    {
        var keep = select ?? (_list.SelectedItem as ListBoxItem)?.Tag switch { TaskItem t => t.Id, MailHeader m => KeyOf(m), _ => null };
        _list.Items.Clear();
        var today = DateTime.Today; var weekEnd = Recurrence.WeekStart(today).AddDays(7);
        var tasks = _store.Tasks.Where(t => _filter switch
        {
            TaskFilter.Active => !t.Completed,
            TaskFilter.Today => !t.Completed && t.DueDate is { } d && d.Date <= today,
            TaskFilter.Week => !t.Completed && t.DueDate is { } d && d.Date < weekEnd,
            TaskFilter.Overdue => t.IsOverdue,
            TaskFilter.Completed => t.Completed,
            _ => true
        }).ToList();
        var flagged = _showFlagged.IsChecked == true && _filter is TaskFilter.Active or TaskFilter.All ? _host.FlaggedMails() : Array.Empty<MailHeader>();
        UpdateFilterCounts();

        string Group(TaskItem t) => t.Completed ? L.T("Tamamlanan") : t.DueDate is not { } d ? L.T("Tarihsiz") : d.Date < today ? L.T("Gecikmiş") : d.Date == today ? L.T("Bugün") :
            d.Date == today.AddDays(1) ? L.T("Yarın") : d.Date < weekEnd ? L.T("Bu hafta") : L.T("Daha sonra");
        var order = new[] { L.T("Gecikmiş"), L.T("Bugün"), L.T("Yarın"), L.T("Bu hafta"), L.T("Daha sonra"), L.T("Tarihsiz"), L.T("Tamamlanan") };
        ListBoxItem? toSelect = null;
        foreach (var g in tasks.GroupBy(Group).OrderBy(g => Array.IndexOf(order, g.Key)))
        {
            Header(g.Key, g.Count(), g.Key == L.T("Gecikmiş"));
            foreach (var t in g.OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenByDescending(t => t.Priority).ThenBy(t => t.Created))
            {
                var item = new ListBoxItem { Content = Row(t), Tag = t };
                _list.Items.Add(item);
                if (t.Id == keep) toSelect = item;
            }
        }
        if (flagged.Count > 0)
        {
            Header(L.T("Bayraklı mailler"), flagged.Count, false);
            foreach (var m in flagged)
            {
                var item = new ListBoxItem { Content = MailRow(m), Tag = m };
                _list.Items.Add(item);
                if (KeyOf(m) == keep) toSelect = item;
            }
        }
        if (_list.Items.Count == 0)
        {
            var empty = PimUi.Text(_filter == TaskFilter.Completed ? L.T("Tamamlanan görev yok.") : L.T("Burada görev yok. Yukarıya yazıp Enter'a basın; bir maili sağ tıklayıp \"Görev olarak ekle\" de diyebilirsiniz."), 12.5, brush: "TextSecondaryBrush");
            empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(14, 20, 14, 0);
            _list.Items.Add(new ListBoxItem { Content = empty, IsEnabled = false });
        }
        if (toSelect != null) { _list.SelectedItem = toSelect; _list.ScrollIntoView(toSelect); }
        else if (_editing == null) ShowDetail(null, null);
    }

    static string KeyOf(MailHeader m) => $"{m.AccountId}|{m.Folder}|{m.Uid}";

    void Header(string text, int count, bool danger)
    {
        var t = PimUi.Text($"{text}  ({count})", 11.5, bold: true, brush: danger ? "StatusDangerBrush" : "TextSecondaryBrush");
        t.Margin = new Thickness(6, 10, 4, 2);
        _list.Items.Add(new ListBoxItem { Content = t, IsEnabled = false, Focusable = false });
    }

    void UpdateFilterCounts()
    {
        var all = _store.Tasks; var today = DateTime.Today; var weekEnd = Recurrence.WeekStart(today).AddDays(7);
        foreach (var b in _filters.Children.OfType<Button>())
        {
            var f = (TaskFilter)b.DataContext;
            var n = f switch
            {
                TaskFilter.Active => all.Count(t => !t.Completed), TaskFilter.Today => all.Count(t => !t.Completed && t.DueDate is { } d && d.Date <= today),
                TaskFilter.Week => all.Count(t => !t.Completed && t.DueDate is { } d && d.Date < weekEnd), TaskFilter.Overdue => all.Count(t => t.IsOverdue),
                TaskFilter.Completed => all.Count(t => t.Completed), _ => all.Count
            };
            var name = f switch
            {
                TaskFilter.Active => L.T("Etkin görevler"), TaskFilter.Today => L.T("Bugün"), TaskFilter.Week => L.T("Bu hafta"), TaskFilter.Overdue => L.T("Gecikmiş"),
                TaskFilter.Completed => L.T("Tamamlanan"), _ => L.T("Tümü")
            };
            b.Content = n > 0 ? $"{name}  ·  {n}" : name;
            b.FontWeight = f == _filter ? FontWeights.SemiBold : FontWeights.Normal;
            if (f == _filter) b.SetResourceReference(BackgroundProperty, "AccentSubtleBrush"); else b.ClearValue(BackgroundProperty);
        }
    }

    FrameworkElement Row(TaskItem t)
    {
        var g = new Grid { Margin = new Thickness(2, 3, 4, 3) };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new());
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var check = new CheckBox { IsChecked = t.Completed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), ToolTip = L.T("Tamamlandı") };
        check.Click += (_, _) => { _store.SetCompleted(t, check.IsChecked == true); _host.Status(t.Completed ? L.F("Tamamlandı: {0}", t.Title) : L.F("Yeniden açıldı: {0}", t.Title)); };
        g.Children.Add(check);
        var sp = new StackPanel();
        var title = PimUi.Text((t.Priority == TaskPriority.High ? "! " : "") + t.Title, 13, bold: false, brush: t.Completed ? "TextMutedBrush" : "TextPrimaryBrush");
        if (t.Completed) title.TextDecorations = TextDecorations.Strikethrough;
        if (t.Priority == TaskPriority.High && !t.Completed) title.SetResourceReference(TextBlock.ForegroundProperty, "StatusDangerBrush");
        sp.Children.Add(title);
        var meta = new List<string>();
        if (t.SourceSubject.Length > 0) meta.Add("✉ " + (t.SourceFrom.Length > 0 ? t.SourceFrom + ": " : "") + t.SourceSubject);
        if (t.ReminderAt is { } r && !t.Completed && !t.ReminderDismissed) meta.Add("\U0001F514 " + r.ToString("g", L.Culture));
        if (t.Notes.Length > 0) meta.Add(t.Notes.Replace("\n", " ").Length > 60 ? t.Notes.Replace("\n", " ")[..60] + "…" : t.Notes.Replace("\n", " "));
        if (meta.Count > 0) sp.Children.Add(PimUi.Text(string.Join("  ·  ", meta), 11, brush: "TextSecondaryBrush"));
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (t.Category.Length > 0) right.Children.Add(new Rectangle { Width = 10, Height = 10, Fill = PimUi.Solid(PimUi.CategoryColor(t.Category)), RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T(t.Category) });
        if (t.DueDate is { } due)
            right.Children.Add(PimUi.Text(DueText(due), 11.5, bold: t.IsOverdue, brush: t.IsOverdue ? "StatusDangerBrush" : t.IsDueToday ? "AccentPrimaryBrush" : "TextSecondaryBrush"));
        Grid.SetColumn(right, 2); g.Children.Add(right);
        g.ContextMenu = TaskMenu(t);
        return g;
    }

    static string DueText(DateTime due)
    {
        var d = due.Date; var today = DateTime.Today;
        if (d == today) return L.T("Bugün");
        if (d == today.AddDays(1)) return L.T("Yarın");
        if (d == today.AddDays(-1)) return L.T("Dün");
        return d.Year == today.Year ? d.ToString("d MMM ddd", L.Culture) : d.ToString("d", L.Culture);
    }

    FrameworkElement MailRow(MailHeader m)
    {
        var g = new Grid { Margin = new Thickness(2, 3, 4, 3) };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new());
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), ToolTip = L.T("Tamamlandı (bayrağı kaldırır)") };
        check.Click += (_, _) => { _host.SetMailFlag(m, false); _host.Status(L.F("Bayrak kaldırıldı: {0}", m.Subject)); Refresh(); };
        g.Children.Add(check);
        var sp = new StackPanel();
        sp.Children.Add(PimUi.Text("⚑ " + (m.Subject.Length > 0 ? m.Subject : L.T("(Konu yok)")), 13));
        sp.Children.Add(PimUi.Text(m.FromDisplay + "  ·  " + m.Date.ToLocalTime().ToString("g", L.Culture), 11, brush: "TextSecondaryBrush"));
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        var menu = new ContextMenu();
        void Item(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); menu.Items.Add(i); }
        Item(L.T("Maili aç"), () => _host.OpenMail(m));
        Item(L.T("Göreve dönüştür"), () => { var t = FromMail(m); _store.Save(t); Refresh(select: t.Id); });
        Item(L.T("Bayrağı kaldır"), () => { _host.SetMailFlag(m, false); Refresh(); });
        g.ContextMenu = menu;
        g.ToolTip = L.T("Çift tıklayın: maili aç");
        return g;
    }

    public static TaskItem FromMail(MailHeader m, DateTime? due = null) => new()
    {
        Title = m.Subject.Length > 0 ? m.Subject : L.T("(Konu yok)"), DueDate = due, SourceMail = $"{m.AccountId}|{m.Folder}|{m.Uid}",
        SourceSubject = m.Subject, SourceFrom = m.FromDisplay,
    };

    ContextMenu TaskMenu(TaskItem t)
    {
        var m = new ContextMenu();
        void Item(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Item(t.Completed ? L.T("Yeniden aç") : L.T("Tamamlandı"), () => _store.SetCompleted(t, !t.Completed));
        Item(L.T("Bugün"), () => { t.DueDate = DateTime.Today; _store.Save(t); });
        Item(L.T("Yarın"), () => { t.DueDate = DateTime.Today.AddDays(1); _store.Save(t); });
        Item(L.T("Gelecek hafta"), () => { t.DueDate = Recurrence.WeekStart(DateTime.Today).AddDays(7); _store.Save(t); });
        Item(L.T("Tarih yok"), () => { t.DueDate = null; _store.Save(t); });
        m.Items.Add(new Separator());
        if (t.SourceMail != null) Item(L.T("Kaynak maili aç"), () => _host.OpenSourceMail(t.SourceMail));
        Item(L.T("Sil"), () => { if (_editing?.Id == t.Id) _editing = null; _store.Delete(t); });
        return m;
    }

    void ToggleSelected()
    {
        switch ((_list.SelectedItem as ListBoxItem)?.Tag)
        {
            case TaskItem t: _store.SetCompleted(t, !t.Completed); break;
            case MailHeader m: _host.SetMailFlag(m, false); Refresh(); break;
        }
    }

    void DeleteSelected()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not TaskItem t) return;
        if (MessageBox.Show(_host.HostWindow, L.F("\"{0}\" silinsin mi?", t.Title), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_editing?.Id == t.Id) _editing = null;
        _store.Delete(t);
    }

    // ===================================================================== detail pane

    TextBox? _dTitle, _dNotes;
    DateTimeField? _dDue, _dStart, _dReminder;
    CheckBox? _dHasDue, _dHasStart, _dHasReminder, _dDone;
    ComboBox? _dPriority, _dCategory;

    void ShowDetail(TaskItem? t, MailHeader? mail)
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveDetail(); }
        _editing = t?.Clone();
        _detail.Children.Clear(); _detail.RowDefinitions.Clear(); _detail.ColumnDefinitions.Clear();
        if (mail != null)
        {
            var sp = new StackPanel();
            sp.Children.Add(PimUi.Text(mail.Subject, 16, bold: true));
            sp.Children.Add(PimUi.Text(mail.FromDisplay + " <" + mail.FromAddress + ">", 12, brush: "TextSecondaryBrush"));
            sp.Children.Add(PimUi.Text(mail.Date.ToLocalTime().ToString("f", L.Culture), 12, brush: "TextSecondaryBrush"));
            var preview = new TextBlock { Text = mail.Preview, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(preview);
            var buttons = new WrapPanel();
            var open = new Button { Content = L.T("Maili aç"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 4, 12, 4) }; open.Click += (_, _) => _host.OpenMail(mail);
            var convert = new Button { Content = L.T("Göreve dönüştür"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 4, 12, 4) };
            convert.Click += (_, _) => { var nt = FromMail(mail); _store.Save(nt); Refresh(select: nt.Id); };
            var unflag = new Button { Content = L.T("Bayrağı kaldır"), Padding = new Thickness(12, 4, 12, 4) }; unflag.Click += (_, _) => { _host.SetMailFlag(mail, false); Refresh(); };
            buttons.Children.Add(open); buttons.Children.Add(convert); buttons.Children.Add(unflag);
            sp.Children.Add(buttons);
            _detail.Children.Add(sp);
            return;
        }
        if (t == null)
        {
            var hint = PimUi.Text(L.T("Ayrıntılarını görmek için bir görev seçin."), 12.5, brush: "TextSecondaryBrush");
            hint.Margin = new Thickness(0, 20, 0, 0); _detail.Children.Add(hint); return;
        }
        _loadingDetail = true;
        _detail.ColumnDefinitions.Add(new() { Width = new GridLength(110) });
        _detail.ColumnDefinitions.Add(new());
        var row = 0;
        void Row(string label, UIElement c, bool stretch = false)
        {
            _detail.RowDefinitions.Add(new() { Height = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            if (label.Length > 0)
            {
                var l = PimUi.Text(label, brush: "TextSecondaryBrush"); l.VerticalAlignment = stretch ? VerticalAlignment.Top : VerticalAlignment.Center; l.Margin = new Thickness(0, 6, 8, 4);
                Grid.SetRow(l, row); _detail.Children.Add(l);
            }
            if (c is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(c, row); Grid.SetColumn(c, label.Length > 0 ? 1 : 0); if (label.Length == 0) Grid.SetColumnSpan(c, 2);
            _detail.Children.Add(c); row++;
        }
        _dTitle = new TextBox { Text = t.Title, FontSize = 15 };
        Row("", _dTitle);
        _dDone = new CheckBox { Content = L.T("Tamamlandı"), IsChecked = t.Completed };
        Row(L.T("Durum"), _dDone);
        (CheckBox, DateTimeField) DateRow(string label, DateTime? value, bool time)
        {
            var cb = new CheckBox { IsChecked = value != null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var f = new DateTimeField(value ?? (time ? DateTime.Today.AddDays(1).AddHours(9) : DateTime.Today), showTime: time) { IsEnabled = value != null };
            cb.Checked += (_, _) => { f.IsEnabled = true; Changed(); }; cb.Unchecked += (_, _) => { f.IsEnabled = false; Changed(); };
            f.Changed += Changed;
            var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(cb); sp.Children.Add(f);
            Row(label, sp);
            return (cb, f);
        }
        (_dHasDue, _dDue) = DateRow(L.T("Son tarih"), t.DueDate, false);
        (_dHasStart, _dStart) = DateRow(L.T("Başlangıç"), t.StartDate, false);
        (_dHasReminder, _dReminder) = DateRow(L.T("Anımsatıcı"), t.ReminderAt, true);
        _dPriority = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (p, text) in new[] { (TaskPriority.Low, L.T("Düşük")), (TaskPriority.Normal, L.T("Normal")), (TaskPriority.High, L.T("Yüksek")) })
            _dPriority.Items.Add(new ComboBoxItem { Content = text, Tag = p });
        _dPriority.SelectedItem = _dPriority.Items.Cast<ComboBoxItem>().First(i => (TaskPriority)i.Tag == t.Priority);
        Row(L.T("Önem"), _dPriority);
        _dCategory = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        _dCategory.Items.Add(new ComboBoxItem { Content = L.T("Yok"), Tag = "" });
        foreach (var (name, color) in PimUi.Categories)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = PimUi.Solid(color), RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = L.T(name) });
            _dCategory.Items.Add(new ComboBoxItem { Content = sp, Tag = name });
        }
        _dCategory.SelectedItem = _dCategory.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == t.Category) ?? _dCategory.Items[0];
        Row(L.T("Kategori"), _dCategory);
        if (t.SourceMail != null)
        {
            var link = new TextBlock();
            var h = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run((t.SourceFrom.Length > 0 ? t.SourceFrom + ": " : "") + (t.SourceSubject.Length > 0 ? t.SourceSubject : L.T("Maili aç"))));
            h.Click += (_, _) => _host.OpenSourceMail(t.SourceMail!);
            link.Inlines.Add(h); link.TextWrapping = TextWrapping.Wrap;
            Row(L.T("Mail"), link);
        }
        _dNotes = new TextBox { Text = t.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Row(L.T("Notlar"), _dNotes, stretch: true);
        var info = PimUi.Text(L.F("Oluşturuldu: {0}", t.Created.ToString("g", L.Culture)) + (t.CompletedAt is { } c ? "  ·  " + L.F("Tamamlandı: {0}", c.ToString("g", L.Culture)) : ""), 11, brush: "TextMutedBrush");
        Row("", info);
        _dTitle.TextChanged += (_, _) => Changed(); _dNotes.TextChanged += (_, _) => Changed();
        _dDone.Click += (_, _) => { _saveTimer.Stop(); SaveDetail(); Refresh(); };
        _dPriority.SelectionChanged += (_, _) => Changed(); _dCategory.SelectionChanged += (_, _) => Changed();
        _loadingDetail = false;
    }

    void Changed() { if (_loadingDetail || _editing == null) return; _saveTimer.Stop(); _saveTimer.Start(); }

    void SaveDetail()
    {
        if (_editing == null || _dTitle == null) return;
        var t = _store.Find(_editing.Id);
        if (t == null) return;
        t.Title = _dTitle.Text.Trim().Length > 0 ? _dTitle.Text.Trim() : t.Title;
        t.Notes = _dNotes!.Text;
        t.Completed = _dDone!.IsChecked == true;
        t.DueDate = _dHasDue!.IsChecked == true && _dDue!.IsValid ? _dDue.Value.Date : null;
        t.StartDate = _dHasStart!.IsChecked == true && _dStart!.IsValid ? _dStart.Value.Date : null;
        var reminder = _dHasReminder!.IsChecked == true && _dReminder!.IsValid ? _dReminder.Value : (DateTime?)null;
        if (reminder != t.ReminderAt) t.ReminderDismissed = false;
        t.ReminderAt = reminder;
        t.Priority = _dPriority!.SelectedItem is ComboBoxItem { Tag: TaskPriority p } ? p : TaskPriority.Normal;
        t.Category = _dCategory!.SelectedItem is ComboBoxItem { Tag: string c } ? c : "";
        _ownSaves++;
        _store.Save(t);
        // Update only the row text, keeping focus in the editor
        foreach (var item in _list.Items.OfType<ListBoxItem>())
            if (item.Tag is TaskItem x && x.Id == t.Id) { item.Content = Row(t); item.Tag = t; }
        UpdateFilterCounts();
    }
}

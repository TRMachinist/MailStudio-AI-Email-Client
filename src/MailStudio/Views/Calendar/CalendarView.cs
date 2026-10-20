using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MailStudio.Services;
using MailStudio.Services.Calendar;
using Microsoft.Win32;

namespace MailStudio.Views.Calendar;

public enum CalendarViewMode { Day, WorkWeek, Week, Month }

/// <summary>What the calendar and task views need from the main window (mail, address book, status bar).</summary>
public interface IPimHost
{
    Window HostWindow { get; }
    string? PickAttendees(Window owner);
    bool IsOwnAddress(string address);
    /// <summary>Opens a compose window with the meeting request / update / cancellation / reply (the user sends it).</summary>
    void ComposeMeetingMail(CalendarEvent e, string method, string? partStat = null, IEnumerable<string>? to = null);
    void OpenSourceMail(string source);
    void Status(string text);
}

/// <summary>Outlook-style calendar: day / work week / week / month, drag to move or resize, double-click to create.</summary>
public sealed class CalendarView : UserControl
{
    public const double HourHeight = 44;
    const int SnapMinutes = 15;
    readonly CalendarStore _store;
    readonly IPimHost _host;
    DateTime _date = DateTime.Today;
    static CalendarViewMode _lastMode = CalendarViewMode.WorkWeek;
    CalendarViewMode _mode = _lastMode;
    readonly Grid _body = new();
    readonly TextBlock _rangeTitle = PimUi.Text("", 18, bold: true);
    readonly MiniMonth _mini = new();
    readonly StackPanel _upcoming = new();
    readonly Dictionary<CalendarViewMode, RadioButton> _modeButtons = new();
    ScrollViewer? _timeScroll;
    double _scrollOffset = -1;
    Occurrence? _selected;
    (DateTime Start, DateTime End)? _slot;
    readonly List<(DateTime Day, Canvas Canvas)> _dayCanvases = new();
    Rectangle? _slotRect;

    /// <summary>The calendar commands shown in the ribbon while the calendar is open.</summary>
    public WrapPanel Ribbon { get; } = new() { Orientation = Orientation.Horizontal };
    /// <summary>Date navigator + upcoming items, shown in the folder pane.</summary>
    public FrameworkElement SidePanel { get; }

    public CalendarView(CalendarStore store, IPimHost host)
    {
        _store = store; _host = host;
        Focusable = true;
        UiTranslator.SetSkip(this, true); UiTranslator.SetSkip(Ribbon, true);
        // ---- ribbon
        Ribbon.Children.Add(PimUi.Group(L.T("Yeni"),
            PimUi.Cmd("", L.T("Yeni Randevu"), (_, _) => NewEvent(meeting: false), L.T("Yeni randevu (Ctrl+N)"), primary: true),
            PimUi.Cmd("", L.T("Yeni Toplantı"), (_, _) => NewEvent(meeting: true), L.T("Katılımcılı toplantı daveti"))));
        Ribbon.Children.Add(PimUi.Group(L.T("Git"),
            PimUi.Cmd("", L.T("Bugün"), (_, _) => GoTo(DateTime.Today), L.T("Bugüne git (Ctrl+T)")),
            PimUi.Cmd("", L.T("Geri"), (_, _) => Move(-1), L.T("Önceki (Alt+Sol)")),
            PimUi.Cmd("", L.T("İleri"), (_, _) => Move(1), L.T("Sonraki (Alt+Sağ)"))));
        var modes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (m, text) in new[] { (CalendarViewMode.Day, L.T("Gün")), (CalendarViewMode.WorkWeek, L.T("İş Haftası")), (CalendarViewMode.Week, L.T("Hafta")), (CalendarViewMode.Month, L.T("Ay")) })
        {
            var rb = PimUi.Segment(text, "calmode", (_, _) => SetMode(m));
            _modeButtons[m] = rb; modes.Children.Add(rb);
        }
        Ribbon.Children.Add(PimUi.Group(L.T("Düzenle"), modes));
        Ribbon.Children.Add(PimUi.Group(L.T("Paylaş"),
            PimUi.Cmd("", L.T("İçe aktar (.ics)"), (_, _) => Import(), L.T("Outlook, Google Takvim veya bir davet dosyasından (.ics)")),
            PimUi.Cmd("", L.T("Dışa aktar"), (_, _) => Export(), L.T("Takvimi .ics dosyası olarak kaydet"))));
        Ribbon.Children.Add(PimUi.Group(L.T("Eşitle"),
            PimUi.Cmd("", L.T("Şimdi Eşitle"), (_, _) => SyncNow(), L.T("Google ve Outlook takvimini şimdi eşitle"))));

        // ---- side panel
        var side = new StackPanel();
        _mini.DatePicked += d => GoTo(d);
        _mini.HasEvents = d => _store.Occurrences(d, d.AddDays(1)).Count > 0;
        side.Children.Add(_mini);
        var upTitle = PimUi.Text(L.T("Yaklaşanlar"), 12, bold: true); upTitle.Margin = new Thickness(12, 10, 8, 4);
        side.Children.Add(upTitle);
        side.Children.Add(_upcoming);
        SidePanel = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UiTranslator.SetSkip(SidePanel, true);

        // ---- view
        var root = new DockPanel();
        var nav = new DockPanel { Margin = new Thickness(14, 8, 14, 6) };
        _rangeTitle.VerticalAlignment = VerticalAlignment.Center;
        nav.Children.Add(_rangeTitle);
        DockPanel.SetDock(nav, Dock.Top); root.Children.Add(nav);
        root.Children.Add(_body);
        Content = root;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");
        _store.Changed += () => Dispatcher.BeginInvoke(Refresh);
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { _modeButtons[_mode].IsChecked = true; Refresh(); Focus(); };
    }

    public CalendarViewMode Mode => _mode;
    public DateTime Date => _date;
    public Func<Task>? SyncHandler { get; set; }

    async void SyncNow()
    {
        if (SyncHandler != null)
        {
            _host.Status(L.T("Takvim eşitleniyor..."));
            try
            {
                await SyncHandler();
                _host.Status(L.T("Takvim eşitlemesi tamamlandı."));
            }
            catch (Exception ex)
            {
                _host.Status(L.T("Takvim eşitleme hatası: ") + ex.Message);
            }
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.Delete && _selected is { } s) { Delete(s); e.Handled = true; }
        else if (e.Key == Key.Enter && _selected is { } o) { Open(o); e.Handled = true; }
        else if (e.Key == Key.Enter && _slot != null) { NewEvent(false); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right) { Move(e.SystemKey == Key.Left ? -1 : 1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T) { GoTo(DateTime.Today); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { NewEvent(false); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D4) { SetMode((CalendarViewMode)(e.Key - Key.D1)); _modeButtons[_mode].IsChecked = true; e.Handled = true; }
    }

    public void SetMode(CalendarViewMode m)
    {
        if (_mode == m && _body.Children.Count > 0) return;
        _mode = _lastMode = m; _selected = null; _slot = null;
        if (_modeButtons.TryGetValue(m, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
        Refresh();
    }

    public void GoTo(DateTime d) { _date = d.Date; _slot = null; Refresh(); }

    void Move(int dir)
    {
        _date = _mode switch
        {
            CalendarViewMode.Day => _date.AddDays(dir),
            CalendarViewMode.Month => new DateTime(_date.Year, _date.Month, 1).AddMonths(dir),
            _ => _date.AddDays(7 * dir)
        };
        Refresh();
    }

    (DateTime From, DateTime To) VisibleRange() => _mode switch
    {
        CalendarViewMode.Day => (_date, _date.AddDays(1)),
        CalendarViewMode.WorkWeek => (Recurrence.WeekStart(_date), Recurrence.WeekStart(_date).AddDays(5)),
        CalendarViewMode.Week => (Recurrence.WeekStart(_date), Recurrence.WeekStart(_date).AddDays(7)),
        _ => MonthRange()
    };

    (DateTime, DateTime) MonthRange()
    {
        var first = Recurrence.WeekStart(new DateTime(_date.Year, _date.Month, 1));
        return (first, first.AddDays(42));
    }

    /// <summary>Re-renders (also called every minute for the "now" line and on store changes).</summary>
    public void Refresh()
    {
        if (_timeScroll != null && _timeScroll.VerticalOffset > 0) _scrollOffset = _timeScroll.VerticalOffset;
        var (from, to) = VisibleRange();
        _rangeTitle.Text = _mode switch
        {
            CalendarViewMode.Day => _date.ToString("D", L.Culture),
            CalendarViewMode.Month => _date.ToString("MMMM yyyy", L.Culture),
            _ => from.Month == to.AddDays(-1).Month
                ? $"{from.Day} – {to.AddDays(-1).ToString("d MMMM yyyy", L.Culture)}"
                : $"{from.ToString("d MMMM", L.Culture)} – {to.AddDays(-1).ToString("d MMMM yyyy", L.Culture)}"
        };
        _mini.Range = _mode == CalendarViewMode.Month ? null : (from, to);
        _mini.Selected = _date;
        _body.Children.Clear(); _body.RowDefinitions.Clear(); _body.ColumnDefinitions.Clear(); _dayCanvases.Clear(); _slotRect = null;
        var occurrences = _store.Occurrences(from, to);
        if (_mode == CalendarViewMode.Month) RenderMonth(from, occurrences); else RenderDays(from, (to - from).Days, occurrences);
        RenderUpcoming();
    }

    // ===================================================================== day / week

    void RenderDays(DateTime first, int count, List<Occurrence> occurrences)
    {
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new());
        Grid Columns()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new() { Width = new GridLength(58) });
            for (var i = 0; i < count; i++) g.ColumnDefinitions.Add(new());
            return g;
        }
        // headers
        var head = Columns();
        for (var i = 0; i < count; i++)
        {
            var day = first.AddDays(i);
            var sp = new StackPanel { Margin = new Thickness(8, 2, 4, 4) };
            var num = PimUi.Text(day.Day.ToString(L.Culture), 20, bold: day == DateTime.Today, brush: day == DateTime.Today ? "AccentPrimaryBrush" : "TextPrimaryBrush");
            var name = PimUi.Text(day.ToString("dddd", L.Culture), 11.5, brush: day == DateTime.Today ? "AccentPrimaryBrush" : "TextSecondaryBrush");
            sp.Children.Add(num); sp.Children.Add(name);
            var b = new Border { Child = sp, BorderThickness = new Thickness(1, 0, 0, day == DateTime.Today ? 3 : 1), Cursor = Cursors.Hand, ToolTip = day.ToString("D", L.Culture) };
            b.SetResourceReference(Border.BorderBrushProperty, day == DateTime.Today ? "AccentPrimaryBrush" : "BorderSubtleBrush");
            b.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { _date = day; SetMode(CalendarViewMode.Day); _modeButtons[CalendarViewMode.Day].IsChecked = true; } };
            Grid.SetColumn(b, i + 1); head.Children.Add(b);
        }
        Grid.SetRow(head, 0); _body.Children.Add(head);

        // all-day band
        var allDay = Columns();
        var allDayItems = occurrences.Where(o => o.Event.AllDay || o.End - o.Start >= TimeSpan.FromHours(24)).ToList();
        var lanes = new List<DateTime>(); // lane → last end
        allDay.RowDefinitions.Add(new() { MinHeight = 24 });
        var laneLabel = PimUi.Text(L.T("Tüm gün"), 10.5, brush: "TextSecondaryBrush"); laneLabel.Margin = new Thickness(4, 4, 2, 2);
        allDay.Children.Add(laneLabel);
        for (var i = 0; i < count; i++)
        {
            var day = first.AddDays(i);
            var cell = new Border { BorderThickness = new Thickness(1, 0, 0, 1), Background = Brushes.Transparent };
            cell.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            cell.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) NewEvent(false, day, day.AddDays(1), allDay: true); };
            cell.ContextMenu = SlotMenu(day, day.AddDays(1), true);
            Grid.SetColumn(cell, i + 1); Grid.SetRowSpan(cell, 50); allDay.Children.Add(cell);
        }
        foreach (var o in allDayItems.OrderBy(o => o.Start).ThenByDescending(o => o.End - o.Start))
        {
            var s = Math.Max(0, (o.Start.Date - first).Days); var e = Math.Min(count, (int)Math.Ceiling((o.End - first).TotalDays));
            if (e <= s) e = s + 1;
            var lane = lanes.FindIndex(end => end <= o.Start);
            if (lane < 0) { lanes.Add(o.End); lane = lanes.Count - 1; if (lane > 0) allDay.RowDefinitions.Add(new() { MinHeight = 24 }); } else lanes[lane] = o.End;
            var chip = Chip(o, compact: true);
            chip.Margin = new Thickness(2, 1, 3, 1);
            Grid.SetRow(chip, lane); Grid.SetColumn(chip, s + 1); Grid.SetColumnSpan(chip, e - s);
            allDay.Children.Add(chip);
        }
        foreach (UIElement c in allDay.Children) if (c is Border { Child: null } cell) Grid.SetRowSpan(cell, Math.Max(1, allDay.RowDefinitions.Count));
        Grid.SetRow(allDay, 1); _body.Children.Add(allDay);

        // time grid
        var grid = Columns();
        grid.Height = HourHeight * 24;
        var ruler = new Canvas();
        for (var h = 0; h < 24; h++)
        {
            var t = PimUi.Text(new DateTime(2000, 1, 1, h, 0, 0).ToString("t", L.Culture), 10.5, brush: "TextSecondaryBrush");
            Canvas.SetTop(t, h * HourHeight + 2); Canvas.SetRight(t, 6); t.Width = 52; t.TextAlignment = TextAlignment.Right;
            ruler.Children.Add(t);
        }
        grid.Children.Add(ruler);
        var timed = occurrences.Where(o => !allDayItems.Contains(o)).ToList();
        for (var i = 0; i < count; i++)
        {
            var day = first.AddDays(i);
            var canvas = new Canvas { ClipToBounds = true, Background = Brushes.Transparent };
            var workStart = new Rectangle { Height = HourHeight * 9, IsHitTestVisible = false };
            var work = day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            workStart.SetResourceReference(Shape.FillProperty, work ? "BgSurfacePrimaryBrush" : "BgSurfaceSecondaryBrush");
            canvas.SetResourceReference(Panel.BackgroundProperty, "BgSurfaceSecondaryBrush");
            Canvas.SetTop(workStart, HourHeight * 8); canvas.Children.Add(workStart);
            for (var h = 0; h < 48; h++)
            {
                var line = new Rectangle { Height = 1, IsHitTestVisible = false, Opacity = h % 2 == 0 ? 1 : 0.45 };
                line.SetResourceReference(Shape.FillProperty, "BorderSubtleBrush");
                Canvas.SetTop(line, h * HourHeight / 2); canvas.Children.Add(line);
                canvas.SizeChanged += (_, _) => line.Width = canvas.ActualWidth;
            }
            var sep = new Border { BorderThickness = new Thickness(1, 0, 0, 0), IsHitTestVisible = false };
            sep.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            var cellHost = new Grid(); cellHost.Children.Add(canvas); cellHost.Children.Add(sep);
            Grid.SetColumn(cellHost, i + 1); grid.Children.Add(cellHost);
            _dayCanvases.Add((day, canvas));
            HookSlots(canvas, day);
            var dayItems = timed.Where(o => o.Start < day.AddDays(1) && o.End > day).ToList();
            canvas.SizeChanged += (_, _) => LayoutDay(canvas, day, dayItems);
            if (day == DateTime.Today)
            {
                var now = new Rectangle { Height = 2, Fill = Brushes.Red, IsHitTestVisible = false };
                var dot = new Ellipse { Width = 9, Height = 9, Fill = Brushes.Red, IsHitTestVisible = false };
                Panel.SetZIndex(now, 50); Panel.SetZIndex(dot, 50);
                var y = (DateTime.Now - day).TotalHours * HourHeight;
                Canvas.SetTop(now, y - 1); Canvas.SetTop(dot, y - 4.5); Canvas.SetLeft(dot, -4);
                canvas.Children.Add(now); canvas.Children.Add(dot);
                canvas.SizeChanged += (_, _) => now.Width = canvas.ActualWidth;
            }
        }
        _timeScroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var target = _scrollOffset >= 0 ? _scrollOffset : HourHeight * Math.Max(0, Math.Min(8, timed.Where(o => o.Start.Date >= first).Select(o => o.Start.Hour).DefaultIfEmpty(8).Min()) - 0.25);
        _timeScroll.Loaded += (_, _) => _timeScroll.ScrollToVerticalOffset(target);
        _timeScroll.ScrollChanged += (_, _) => { if (_timeScroll.VerticalOffset > 0) _scrollOffset = _timeScroll.VerticalOffset; };
        Grid.SetRow(_timeScroll, 2); _body.Children.Add(_timeScroll);
    }

    /// <summary>Places the day's events in side-by-side lanes when they overlap (Outlook layout).</summary>
    void LayoutDay(Canvas canvas, DateTime day, List<Occurrence> items)
    {
        foreach (var old in canvas.Children.OfType<Border>().Where(b => b.Tag is Occurrence).ToList()) canvas.Children.Remove(old);
        var width = canvas.ActualWidth - 8;
        if (width <= 10) return;
        var sorted = items.OrderBy(o => o.Start).ThenByDescending(o => o.End).ToList();
        var clusters = new List<List<Occurrence>>();
        DateTime clusterEnd = DateTime.MinValue;
        foreach (var o in sorted)
        {
            var end = Max(o.End, o.Start.AddMinutes(25));
            if (clusters.Count == 0 || o.Start >= clusterEnd) { clusters.Add(new()); clusterEnd = end; }
            else if (end > clusterEnd) clusterEnd = end;
            clusters[^1].Add(o);
        }
        foreach (var cluster in clusters)
        {
            var laneEnds = new List<DateTime>(); var lanes = new Dictionary<Occurrence, int>();
            foreach (var o in cluster)
            {
                var lane = laneEnds.FindIndex(e => e <= o.Start);
                var end = Max(o.End, o.Start.AddMinutes(25));
                if (lane < 0) { laneEnds.Add(end); lane = laneEnds.Count - 1; } else laneEnds[lane] = end;
                lanes[o] = lane;
            }
            var laneWidth = width / laneEnds.Count;
            foreach (var o in cluster)
            {
                var s = Max(o.Start, day); var e = Min(o.End, day.AddDays(1));
                var top = (s - day).TotalHours * HourHeight;
                var height = Math.Max(HourHeight / 2 - 2, (e - s).TotalHours * HourHeight - 2);
                var block = Chip(o, compact: height < HourHeight * 0.75);
                block.Width = Math.Max(20, laneWidth - 2); block.Height = height;
                Canvas.SetLeft(block, 1 + lanes[o] * laneWidth); Canvas.SetTop(block, top + 1);
                Panel.SetZIndex(block, 10 + lanes[o]);
                HookDrag(block, o, day);
                canvas.Children.Add(block);
            }
        }
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    Border Chip(Occurrence o, bool compact)
    {
        var e = o.Event;
        var color = PimUi.CategoryColor(e.Category);
        var text = new StackPanel { Margin = new Thickness(6, 2, 4, 2) };
        var title = PimUi.Text((e.Cancelled ? L.T("İptal edildi: ") : "") + e.Title, 12, bold: true);
        if (e.Cancelled) title.TextDecorations = TextDecorations.Strikethrough;
        var icons = (e.Repeat != null ? "↻ " : "") + (e.IsMeeting ? "\U0001F465 " : "") + (e.Private ? "\U0001F512 " : "");
        if (icons.Length > 0) title.Text = icons + title.Text;
        text.Children.Add(title);
        if (!compact)
        {
            var sub = PimUi.Text(PimUi.RangeText(o) + (e.Location.Length > 0 ? "  ·  " + e.Location : ""), 11, brush: "TextSecondaryBrush");
            text.Children.Add(sub);
        }
        var b = new Border
        {
            Child = text, Tag = o, BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
            BorderBrush = PimUi.Solid(color), Background = PimUi.Tint(color, e.Response == MeetingResponse.Tentative || e.Busy == BusyStatus.Tentative ? false : true),
            ToolTip = $"{e.Title}\n{PimUi.RangeText(o)}" + (e.Location.Length > 0 ? "\n" + e.Location : "") + (e.Organizer.Length > 0 ? "\n" + L.T("Düzenleyen: ") + e.Organizer : "") +
                      (e.Notes.Length > 0 ? "\n\n" + (e.Notes.Length > 300 ? e.Notes[..300] + "…" : e.Notes) : ""),
        };
        if (e.Response == MeetingResponse.None && e.Organizer.Length > 0 && !e.Cancelled) { b.Opacity = 0.8; b.BorderThickness = new Thickness(4, 1, 1, 1); }
        if (_selected is { } sel && sel.Key == o.Key) { b.BorderThickness = new Thickness(4, 2, 2, 2); }
        b.MouseLeftButtonDown += (_, ev) =>
        {
            Focus();
            if (ev.ClickCount == 2) { ev.Handled = true; Open(o); return; }
            Select(o);
        };
        b.ContextMenu = EventMenu(o);
        return b;
    }

    void Select(Occurrence o)
    {
        _selected = o; _slot = null;
        if (_slotRect != null) _slotRect.Visibility = Visibility.Collapsed;
        foreach (var b in FindChips(this)) b.BorderThickness = b.Tag is Occurrence x && x.Key == o.Key ? new Thickness(4, 2, 2, 2) : new Thickness(4, 0, 0, 0);
    }

    static IEnumerable<Border> FindChips(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Border { Tag: Occurrence } b) yield return b;
            foreach (var x in FindChips(c)) yield return x;
        }
    }

    // ---- empty-slot selection and creation
    void HookSlots(Canvas canvas, DateTime day)
    {
        DateTime? anchor = null;
        DateTime At(Point p) => day.AddMinutes(Math.Clamp(Math.Floor(p.Y / (HourHeight / 2)) * 30, 0, 24 * 60 - 30));
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != canvas) return;
            Focus();
            var t = At(e.GetPosition(canvas));
            if (e.ClickCount == 2) { NewEvent(false, _slot?.Start ?? t, _slot?.End ?? t.AddMinutes(30)); e.Handled = true; return; }
            anchor = t; ShowSlot(canvas, t, t.AddMinutes(30)); canvas.CaptureMouse();
        };
        canvas.MouseMove += (_, e) =>
        {
            if (anchor is not { } a || e.LeftButton != MouseButtonState.Pressed) return;
            var t = At(e.GetPosition(canvas));
            ShowSlot(canvas, t < a ? t : a, (t < a ? a : t).AddMinutes(30));
        };
        canvas.MouseLeftButtonUp += (_, _) => { anchor = null; canvas.ReleaseMouseCapture(); };
        canvas.MouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource != canvas) return;
            var t = At(e.GetPosition(canvas));
            if (_slot is not { } s || t < s.Start || t >= s.End) ShowSlot(canvas, t, t.AddMinutes(30));
            canvas.ContextMenu = SlotMenu(_slot!.Value.Start, _slot.Value.End, false);
        };
    }

    void ShowSlot(Canvas canvas, DateTime start, DateTime end)
    {
        _slot = (start, end); _selected = null;
        if (_slotRect != null && _slotRect.Parent is Canvas old) old.Children.Remove(_slotRect);
        _slotRect = new Rectangle { IsHitTestVisible = false, RadiusX = 2, RadiusY = 2 };
        _slotRect.SetResourceReference(Shape.FillProperty, "AccentSubtleBrush");
        _slotRect.SetResourceReference(Shape.StrokeProperty, "AccentPrimaryBrush");
        Canvas.SetTop(_slotRect, (start - start.Date).TotalHours * HourHeight);
        _slotRect.Height = (end - start).TotalHours * HourHeight; _slotRect.Width = Math.Max(0, canvas.ActualWidth - 2);
        Panel.SetZIndex(_slotRect, 5);
        canvas.Children.Add(_slotRect);
    }

    ContextMenu SlotMenu(DateTime start, DateTime end, bool allDay)
    {
        var m = new ContextMenu();
        void Item(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Item(L.T("Yeni randevu"), () => NewEvent(false, start, end, allDay));
        Item(L.T("Yeni tüm gün etkinliği"), () => NewEvent(false, start.Date, start.Date.AddDays(1), true));
        Item(L.T("Yeni toplantı"), () => NewEvent(true, start, end, allDay));
        m.Items.Add(new Separator());
        Item(L.T("Bugüne git"), () => GoTo(DateTime.Today));
        return m;
    }

    // ---- drag to move / resize
    void HookDrag(Border block, Occurrence o, DateTime day)
    {
        Point? down = null; bool dragging = false, resizing = false; double startTop = 0, startHeight = 0;
        block.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(block);
            if (down == null) { block.Cursor = p.Y > block.ActualHeight - 6 ? Cursors.SizeNS : Cursors.Hand; return; }
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var canvas = (Canvas)block.Parent;
            var cp = e.GetPosition(canvas);
            if (!dragging && (Math.Abs(cp.Y - down.Value.Y) > 4 || Math.Abs(cp.X - down.Value.X) > 4))
            {
                if (o.Event.Organizer.Length > 0 && !_host.IsOwnAddress(Ics.AddressOf(o.Event.Organizer))) { down = null; block.ReleaseMouseCapture(); _host.Status(L.T("Başkasının düzenlediği toplantı taşınamaz; düzenleyenden yeni saat isteyin.")); return; }
                dragging = true; block.Opacity = 0.75; Panel.SetZIndex(block, 100);
            }
            if (!dragging) return;
            var dy = cp.Y - down.Value.Y;
            if (resizing) block.Height = Math.Max(HourHeight / 4, startHeight + dy);
            else Canvas.SetTop(block, startTop + dy);
        };
        block.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount > 1) return;
            var canvas = (Canvas)block.Parent;
            down = e.GetPosition(canvas); startTop = Canvas.GetTop(block); startHeight = block.ActualHeight;
            resizing = e.GetPosition(block).Y > block.ActualHeight - 6; dragging = false;
            block.CaptureMouse();
        };
        block.MouseLeftButtonUp += (_, e) =>
        {
            block.ReleaseMouseCapture();
            if (!dragging || down == null) { down = null; return; }
            var canvas = (Canvas)block.Parent;
            var cp = e.GetPosition(canvas);
            var minutes = Snap((cp.Y - down.Value.Y) / HourHeight * 60);
            down = null; dragging = false; block.Opacity = 1;
            if (resizing)
            {
                var newEnd = o.End.AddMinutes(minutes);
                if (newEnd <= o.Start.AddMinutes(SnapMinutes)) newEnd = o.Start.AddMinutes(SnapMinutes);
                ApplyMove(o, o.Start, newEnd);
            }
            else
            {
                // horizontal: which day column is under the mouse
                var target = day;
                var root = e.GetPosition(_body);
                foreach (var (d, c) in _dayCanvases)
                {
                    var origin = c.TranslatePoint(new Point(0, 0), _body);
                    if (root.X >= origin.X && root.X < origin.X + c.ActualWidth) { target = d; break; }
                }
                var shift = (target - day) + TimeSpan.FromMinutes(minutes);
                if (shift == TimeSpan.Zero) { Refresh(); return; }
                ApplyMove(o, o.Start + shift, o.End + shift);
            }
        };
    }

    static int Snap(double minutes) => (int)(Math.Round(minutes / SnapMinutes) * SnapMinutes);

    void ApplyMove(Occurrence o, DateTime start, DateTime end)
    {
        var e = o.Event;
        if (e.Repeat != null)
        {
            // Moving one occurrence of a series detaches it (Outlook)
            var copy = Detach(o);
            copy.Start = start; copy.End = end;
            _store.Save(copy);
        }
        else { e.Start = start; e.End = end; e.Sequence++; _store.Save(e); }
        _host.Status(L.F("{0} → {1}", e.Title, PimUi.RangeText(new Occurrence(e, start, end))));
        if (e.IsMeeting && (e.Organizer.Length == 0 || _host.IsOwnAddress(Ics.AddressOf(e.Organizer))) && e.Attendees.Count > 0 &&
            MessageBox.Show(_host.HostWindow, L.T("Toplantının saati değişti. Katılımcılara güncelleme gönderilsin mi?"), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _host.ComposeMeetingMail(_store.Events.FirstOrDefault(x => x.Start == start && x.Uid.StartsWith(e.Uid, StringComparison.Ordinal)) ?? e, "REQUEST");
    }

    /// <summary>Turns one occurrence into its own event and hides it in the series.</summary>
    CalendarEvent Detach(Occurrence o)
    {
        var master = o.Event;
        var copy = master.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Uid = master.Uid + "#" + o.Start.ToString("yyyyMMddTHHmm", System.Globalization.CultureInfo.InvariantCulture);
        copy.Repeat = null; copy.Exceptions = new(); copy.Start = o.Start; copy.End = o.End; copy.Sequence = master.Sequence + 1;
        master.Exceptions.Add(o.Start);
        _store.Save(master);
        return copy;
    }

    // ===================================================================== month

    void RenderMonth(DateTime first, List<Occurrence> occurrences)
    {
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new());
        var head = new UniformGrid { Columns = 7, Rows = 1 };
        for (var i = 0; i < 7; i++)
        {
            var t = PimUi.Text(L.Culture.DateTimeFormat.GetDayName((DayOfWeek)((i + 1) % 7)), 12, brush: "TextSecondaryBrush");
            t.Margin = new Thickness(8, 2, 4, 4); head.Children.Add(t);
        }
        _body.Children.Add(head);
        var grid = new UniformGrid { Columns = 7, Rows = 6 };
        for (var i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            var inMonth = day.Month == _date.Month;
            var items = occurrences.Where(o => o.Start < day.AddDays(1) && (o.End > day || (o.End == o.Start && o.Start.Date == day))).ToList();
            var cell = new DockPanel { LastChildFill = true };
            var label = PimUi.Text(day.Day == 1 ? day.ToString("d MMM", L.Culture) : day.Day.ToString(L.Culture), 12.5, bold: day == DateTime.Today || day.Day == 1,
                brush: day == DateTime.Today ? "AccentPrimaryBrush" : inMonth ? "TextPrimaryBrush" : "TextMutedBrush");
            label.Margin = new Thickness(6, 3, 4, 2);
            DockPanel.SetDock(label, Dock.Top); cell.Children.Add(label);
            var list = new StackPanel();
            const int max = 4;
            foreach (var o in items.Take(items.Count > max ? max - 1 : max))
            {
                var chip = Chip(o, compact: true);
                if (!o.Event.AllDay && o.End - o.Start < TimeSpan.FromHours(24) && chip.Child is StackPanel sp && sp.Children[0] is TextBlock tb)
                    tb.Text = PimUi.TimeText(o.Start) + "  " + tb.Text;
                chip.Margin = new Thickness(3, 1, 3, 1); chip.Height = 20;
                list.Children.Add(chip);
            }
            if (items.Count > max)
            {
                var more = new Button { Content = L.F("+{0} daha", items.Count - (max - 1)), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 0, 6, 0), Height = 20, Margin = new Thickness(3, 0, 0, 0) };
                more.SetResourceReference(StyleProperty, "SubtleButton");
                more.Click += (_, _) => { _date = day; SetMode(CalendarViewMode.Day); _modeButtons[CalendarViewMode.Day].IsChecked = true; };
                list.Children.Add(more);
            }
            cell.Children.Add(list);
            var border = new Border { Child = cell, BorderThickness = new Thickness(1, 1, 0, 0) };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            border.SetResourceReference(Border.BackgroundProperty, inMonth ? "BgSurfacePrimaryBrush" : "BgSurfaceSecondaryBrush");
            if (day == DateTime.Today) border.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2 && e.OriginalSource is not TextBlock { Parent: StackPanel { Parent: Border { Tag: Occurrence } } }) { NewEvent(false, day, day.AddDays(1), allDay: true); e.Handled = true; }
                else _date = day;
            };
            border.ContextMenu = SlotMenu(day.AddHours(9), day.AddHours(9.5), false);
            grid.Children.Add(border);
        }
        Grid.SetRow(grid, 1); _body.Children.Add(grid);
    }

    // ===================================================================== side list

    void RenderUpcoming()
    {
        _upcoming.Children.Clear();
        var now = DateTime.Now;
        var items = _store.Occurrences(now.Date, now.Date.AddDays(8), includeCancelled: false).Where(o => o.End > now).Take(12).ToList();
        if (items.Count == 0)
        {
            var t = PimUi.Text(L.T("Önümüzdeki 7 günde etkinlik yok."), 11.5, brush: "TextSecondaryBrush"); t.Margin = new Thickness(12, 0, 8, 0); t.TextWrapping = TextWrapping.Wrap;
            _upcoming.Children.Add(t); return;
        }
        DateTime? lastDay = null;
        foreach (var o in items)
        {
            if (lastDay != o.Start.Date)
            {
                lastDay = o.Start.Date;
                var d = PimUi.Text(o.Start.Date == DateTime.Today ? L.T("Bugün") : o.Start.Date == DateTime.Today.AddDays(1) ? L.T("Yarın") : o.Start.ToString("dddd, d MMMM", L.Culture), 11, bold: true, brush: "TextSecondaryBrush");
                d.Margin = new Thickness(12, 6, 8, 2); _upcoming.Children.Add(d);
            }
            var row = new Border { Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 1, 8, 1), BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = PimUi.Solid(PimUi.CategoryColor(o.Event.Category)), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var sp = new StackPanel();
            sp.Children.Add(PimUi.Text(o.Event.Title, 12, bold: true));
            sp.Children.Add(PimUi.Text(PimUi.RangeText(o) + (o.Event.Location.Length > 0 ? " · " + o.Event.Location : ""), 11, brush: "TextSecondaryBrush"));
            row.Child = sp;
            row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Open(o); else { _date = o.Start.Date; if (_mode == CalendarViewMode.Month) Refresh(); else GoTo(o.Start.Date); } };
            _upcoming.Children.Add(row);
        }
    }

    // ===================================================================== commands

    ContextMenu EventMenu(Occurrence o)
    {
        var e = o.Event;
        var m = new ContextMenu();
        MenuItem Item(string h, Action a, ItemsControl? parent = null) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); (parent ?? m).Items.Add(i); return i; }
        Item(L.T("Aç"), () => Open(o)).FontWeight = FontWeights.SemiBold;
        var invited = e.Organizer.Length > 0 && !_host.IsOwnAddress(Ics.AddressOf(e.Organizer));
        if (invited && !e.Cancelled)
        {
            m.Items.Add(new Separator());
            Item(L.T("Kabul et"), () => Respond(e, MeetingResponse.Accepted));
            Item(L.T("Belki"), () => Respond(e, MeetingResponse.Tentative));
            Item(L.T("Reddet"), () => Respond(e, MeetingResponse.Declined));
        }
        m.Items.Add(new Separator());
        var cat = new MenuItem { Header = L.T("Kategori") }; m.Items.Add(cat);
        Item(L.T("Yok"), () => { e.Category = ""; _store.Save(e); }, cat);
        foreach (var (name, color) in PimUi.Categories)
        {
            var mi = Item(L.T(name), () => { e.Category = name; _store.Save(e); }, cat);
            mi.Icon = new Rectangle { Width = 12, Height = 12, Fill = PimUi.Solid(color), RadiusX = 2, RadiusY = 2 };
            mi.IsChecked = string.Equals(e.Category, name, StringComparison.OrdinalIgnoreCase);
        }
        var busy = new MenuItem { Header = L.T("Durum") }; m.Items.Add(busy);
        foreach (var (b, t) in new[] { (BusyStatus.Busy, L.T("Meşgul")), (BusyStatus.Tentative, L.T("Kesin değil")), (BusyStatus.Free, L.T("Serbest")), (BusyStatus.OutOfOffice, L.T("Ofis dışında")) })
            Item(t, () => { e.Busy = b; _store.Save(e); }, busy).IsChecked = e.Busy == b;
        if (e.SourceMail != null) Item(L.T("Kaynak maili aç"), () => _host.OpenSourceMail(e.SourceMail!));
        Item(L.T("Kopyala (metin)"), () => Clipboard.SetText($"{e.Title}\n{PimUi.RangeText(o)}" + (e.Location.Length > 0 ? "\n" + e.Location : "")));
        m.Items.Add(new Separator());
        Item(L.T("Sil"), () => Delete(o));
        return m;
    }

    void Respond(CalendarEvent e, MeetingResponse response)
    {
        e.Response = response;
        if (response == MeetingResponse.Tentative) e.Busy = BusyStatus.Tentative; else if (response == MeetingResponse.Accepted && e.Busy == BusyStatus.Tentative) e.Busy = BusyStatus.Busy;
        _store.Save(e);
        if (MessageBox.Show(_host.HostWindow, L.T("Düzenleyene yanıt gönderilsin mi? (Mail penceresi açılır, siz gönderirsiniz.)"), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _host.ComposeMeetingMail(e, "REPLY", response switch { MeetingResponse.Accepted => "ACCEPTED", MeetingResponse.Tentative => "TENTATIVE", _ => "DECLINED" });
    }

    public void NewEvent(bool meeting) => NewEvent(meeting, _slot?.Start, _slot?.End);

    public void NewEvent(bool meeting, DateTime? start, DateTime? end, bool allDay = false)
    {
        var s = start ?? NextHalfHour(_mode == CalendarViewMode.Month || _date != DateTime.Today ? _date.AddHours(9) : DateTime.Now);
        var e = new CalendarEvent { Start = s, End = end ?? s.AddMinutes(30), AllDay = allDay };
        if (allDay) { e.End = e.Start.Date == e.End.Date ? e.Start.Date.AddDays(1) : e.End; e.Busy = BusyStatus.Free; e.ReminderMinutes = 720; }
        if (allDay) e.End = e.End.AddDays(-1); // the form shows the inclusive last day
        Edit(e, isNew: true, meeting: meeting);
    }

    static DateTime NextHalfHour(DateTime t)
    {
        var m = t.Minute < 30 ? 30 : 60;
        return t.Date.AddHours(t.Hour).AddMinutes(m);
    }

    public void Open(Occurrence o)
    {
        var e = o.Event;
        if (e.Repeat != null)
        {
            var r = MessageBox.Show(_host.HostWindow, L.F("\"{0}\" yinelenen bir öğe.\n\nEvet: yalnızca bu oluşumu ({1}) aç\nHayır: tüm seriyi aç", e.Title, o.Start.ToString("g", L.Culture)),
                L.T("Yinelenen öğeyi aç"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes)
            {
                var copy = e.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Repeat = null; copy.Exceptions = new();
                copy.Start = o.Start; copy.End = o.End;
                if (copy.AllDay) copy.End = copy.End.AddDays(-1);
                var w = Form(copy, isNew: false, meeting: copy.IsMeeting);
                if (w.ShowDialog() != true) return;
                if (w.Deleted) { _store.DeleteOccurrence(e, o.Start); return; }
                copy.Uid = e.Uid + "#" + o.Start.ToString("yyyyMMddTHHmm", System.Globalization.CultureInfo.InvariantCulture);
                e.Exceptions.Add(o.Start); _store.Save(e); _store.Save(copy);
                if (w.SendInvitations) _host.ComposeMeetingMail(copy, "REQUEST");
                return;
            }
        }
        var display = e.Clone();
        if (display.AllDay) display.End = display.End.AddDays(-1);
        Edit(display, isNew: false, meeting: e.IsMeeting, original: e);
    }

    EventWindow Form(CalendarEvent e, bool isNew, bool meeting) =>
        new(e, _store, isNew, meeting) { Owner = _host.HostWindow, PickAttendees = _host.PickAttendees, IsOwnAddress = _host.IsOwnAddress };

    void Edit(CalendarEvent e, bool isNew, bool meeting, CalendarEvent? original = null)
    {
        var w = Form(e, isNew, meeting);
        if (w.ShowDialog() != true) return;
        if (w.Deleted && original != null) { Delete(new Occurrence(original, original.Start, original.End), confirm: false); return; }
        _store.Save(e);
        _selected = null;
        if (!isNew || e.Start < VisibleRange().From || e.Start >= VisibleRange().To) GoTo(e.Start.Date);
        if (w.SendInvitations) _host.ComposeMeetingMail(e, "REQUEST");
    }

    public void Delete(Occurrence o, bool confirm = true)
    {
        var e = o.Event;
        var occurrenceOnly = false;
        if (e.Repeat != null)
        {
            var r = MessageBox.Show(_host.HostWindow, L.F("\"{0}\" yinelenen bir öğe.\n\nEvet: yalnızca bu oluşumu ({1}) sil\nHayır: tüm seriyi sil", e.Title, o.Start.ToString("g", L.Culture)),
                L.T("Yinelenen öğeyi sil"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            occurrenceOnly = r == MessageBoxResult.Yes;
        }
        else if (confirm && MessageBox.Show(_host.HostWindow, L.F("\"{0}\" silinsin mi?", e.Title), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var own = e.Organizer.Length == 0 || _host.IsOwnAddress(Ics.AddressOf(e.Organizer));
        if (e.IsMeeting && !e.Cancelled && e.Attendees.Count > 0)
        {
            if (own && MessageBox.Show(_host.HostWindow, L.T("Katılımcılara iptal bildirimi gönderilsin mi? (Mail penceresi açılır, siz gönderirsiniz.)"), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _host.ComposeMeetingMail(occurrenceOnly ? OccurrenceCopy(o) : e, "CANCEL");
            else if (!own && e.Response != MeetingResponse.Declined && MessageBox.Show(_host.HostWindow, L.T("Düzenleyene \"Reddedildi\" yanıtı gönderilsin mi?"), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _host.ComposeMeetingMail(e, "REPLY", "DECLINED");
        }
        if (occurrenceOnly) _store.DeleteOccurrence(e, o.Start); else _store.Delete(e);
        _selected = null;
        _host.Status(L.F("Silindi: {0}", e.Title));
    }

    static CalendarEvent OccurrenceCopy(Occurrence o)
    {
        var c = o.Event.Clone(); c.Repeat = null; c.Start = o.Start; c.End = o.End; return c;
    }

    void Import()
    {
        var d = new OpenFileDialog { Filter = L.T("iCalendar") + " (*.ics)|*.ics|" + L.T("Tüm dosyalar") + "|*.*", Multiselect = true };
        if (d.ShowDialog(_host.HostWindow) != true) return;
        int added = 0, updated = 0;
        foreach (var f in d.FileNames)
        {
            try { var (a, u) = _store.ImportIcs(f); added += a; updated += u; }
            catch (Exception ex) { MessageBox.Show(_host.HostWindow, System.IO.Path.GetFileName(f) + ": " + ex.Message, L.T("İçe aktar (.ics)")); }
        }
        _host.Status(L.F("Takvime {0} öğe eklendi, {1} öğe güncellendi.", added, updated));
        var first = _store.Events.OrderByDescending(e => e.Modified).FirstOrDefault();
        if (added > 0 && first != null) GoTo(first.Start.Date);
    }

    void Export()
    {
        var d = new SaveFileDialog { Filter = L.T("iCalendar") + " (*.ics)|*.ics", FileName = L.T("MailStudio takvim") + ".ics" };
        if (d.ShowDialog(_host.HostWindow) != true) return;
        try { _store.ExportIcs(d.FileName); _host.Status(L.F("{0} öğe dışa aktarıldı: {1}", _store.Events.Count, d.FileName)); }
        catch (Exception ex) { MessageBox.Show(_host.HostWindow, ex.Message, L.T("Dışa aktar")); }
    }
}

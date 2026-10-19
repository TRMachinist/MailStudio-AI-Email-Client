using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MailStudio.Services;
using MailStudio.Services.Calendar;

namespace MailStudio.Views.Calendar;

/// <summary>Appointment / meeting form (Outlook "Appointment" and "Meeting" windows).</summary>
public sealed class EventWindow : Window
{
    readonly CalendarEvent _e;
    readonly CalendarStore _store;
    readonly TextBox _title = new() { FontSize = 15 };
    readonly TextBox _location = new();
    readonly TextBox _attendees = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 28 };
    readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    readonly DateTimeField _start, _end;
    readonly CheckBox _allDay = new() { Content = L.T("Tüm gün"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly CheckBox _private = new() { Content = L.T("Özel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly ComboBox _repeat = new() { MinWidth = 230 };
    readonly CheckBox _hasUntil = new() { Content = L.T("Bitiş tarihi"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
    readonly DateTimeField _until;
    readonly ComboBox _reminder = new() { MinWidth = 150 };
    readonly ComboBox _busy = new() { MinWidth = 130 };
    readonly ComboBox _category = new() { MinWidth = 130 };
    readonly TextBlock _conflict = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBlock _repeatText = new() { Margin = new Thickness(0, 4, 0, 0), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    readonly Button _send;
    readonly bool _isNew;

    /// <summary>The user pressed Send: the caller mails the invitation (or update) to the attendees.</summary>
    public bool SendInvitations { get; private set; }
    /// <summary>The user deleted the event (a meeting they organise: the caller offers to send a cancellation).</summary>
    public bool Deleted { get; private set; }
    public CalendarEvent Event => _e;
    /// <summary>Opens the address book; returns "Name &lt;addr&gt;; ..." to append, or null.</summary>
    public Func<Window, string?>? PickAttendees { get; set; }

    public EventWindow(CalendarEvent e, CalendarStore store, bool isNew, bool meeting)
    {
        _e = e; _store = store; _isNew = isNew;
        UiTranslator.SetSkip(this, true);
        Title = (isNew ? (meeting ? L.T("Yeni toplantı") : L.T("Yeni randevu")) : e.Title.Length > 0 ? e.Title : L.T("Randevu")) + " - MailStudio";
        Width = 760; Height = 640; MinWidth = 600; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");
        _start = new DateTimeField(e.Start); _end = new DateTimeField(e.End);
        _until = new DateTimeField(e.Repeat?.Until ?? e.Start.AddMonths(3), showTime: false);

        var root = new DockPanel();
        // ---- command bar
        var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
        bar.SetResourceReference(Panel.BackgroundProperty, "BgSurfaceSecondaryBrush");
        _send = PimUi.Cmd("", L.T("Gönder"), (_, _) => Finish(send: true), L.T("Kaydet ve katılımcılara davet gönder (taslak olarak açılır, siz gönderirsiniz)"), primary: true);
        bar.Children.Add(_send);
        bar.Children.Add(PimUi.Cmd("", L.T("Kaydet ve Kapat"), (_, _) => Finish(send: false), L.T("Kaydet (Ctrl+S)"), primary: false));
        if (!isNew) bar.Children.Add(PimUi.Cmd("", L.T("Sil"), (_, _) => { Deleted = true; DialogResult = true; }));
        bar.Children.Add(PimUi.Cmd("", L.T("Katılımcı ekle"), (_, _) => AddAttendees(), L.T("Adres defterinden kişi seç")));
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        DockPanel.SetDock(barBorder, Dock.Top); root.Children.Add(barBorder);

        // ---- form
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new());
        int row = 0;
        void Row(string label, UIElement content, bool stretch = false)
        {
            grid.RowDefinitions.Add(new() { Height = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, MinHeight = stretch ? 80 : 0 });
            var l = PimUi.Text(label, brush: "TextSecondaryBrush"); l.VerticalAlignment = stretch ? VerticalAlignment.Top : VerticalAlignment.Center; l.Margin = new Thickness(0, stretch ? 6 : 4, 8, 4);
            Grid.SetRow(l, row); grid.Children.Add(l);
            if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(content, row); Grid.SetColumn(content, 1); grid.Children.Add(content);
            row++;
        }
        if (e.Organizer.Length > 0 && !isNew)
        {
            var org = PimUi.Text(e.Organizer + (e.Response switch
            {
                MeetingResponse.Accepted => "  ·  " + L.T("Kabul ettiniz"), MeetingResponse.Tentative => "  ·  " + L.T("Belki dediniz"),
                MeetingResponse.Declined => "  ·  " + L.T("Reddettiniz"), _ => ""
            }));
            Row(L.T("Düzenleyen"), org);
        }
        Row(L.T("Katılımcılar"), _attendees);
        Row(L.T("Konu"), _title);
        Row(L.T("Konum"), _location);
        var startRow = new StackPanel { Orientation = Orientation.Horizontal };
        startRow.Children.Add(_start); startRow.Children.Add(_allDay);
        Row(L.T("Başlangıç"), startRow);
        Row(L.T("Bitiş"), _end);
        var repeatRow = new StackPanel();
        var repeatLine = new StackPanel { Orientation = Orientation.Horizontal };
        repeatLine.Children.Add(_repeat); repeatLine.Children.Add(_hasUntil); repeatLine.Children.Add(_until);
        repeatRow.Children.Add(repeatLine); repeatRow.Children.Add(_repeatText);
        _repeatText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Row(L.T("Yinele"), repeatRow);
        var options = new WrapPanel();
        options.Children.Add(Labeled(L.T("Anımsatıcı"), _reminder));
        options.Children.Add(Labeled(L.T("Durum"), _busy));
        options.Children.Add(Labeled(L.T("Kategori"), _category));
        options.Children.Add(_private);
        Row(L.T("Seçenekler"), options);
        Row(L.T("Notlar"), _notes, stretch: true);
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(_conflict, row); Grid.SetColumn(_conflict, 1); grid.Children.Add(_conflict);
        _conflict.SetResourceReference(TextBlock.ForegroundProperty, "StatusDangerBrush");
        root.Children.Add(grid);
        Content = root;

        // ---- values
        _title.Text = e.Title; _location.Text = e.Location; _notes.Text = e.Notes; _attendees.Text = string.Join("; ", e.Attendees);
        _allDay.IsChecked = e.AllDay; _private.IsChecked = e.Private;
        foreach (var m in PimUi.ReminderChoices) _reminder.Items.Add(new ComboBoxItem { Content = PimUi.ReminderText(m), Tag = m });
        if (!PimUi.ReminderChoices.Contains(e.ReminderMinutes)) _reminder.Items.Add(new ComboBoxItem { Content = PimUi.ReminderText(e.ReminderMinutes), Tag = e.ReminderMinutes });
        _reminder.SelectedItem = _reminder.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == e.ReminderMinutes);
        foreach (var (b, t) in new[] { (BusyStatus.Busy, L.T("Meşgul")), (BusyStatus.Tentative, L.T("Kesin değil")), (BusyStatus.Free, L.T("Serbest")), (BusyStatus.OutOfOffice, L.T("Ofis dışında")) })
            _busy.Items.Add(new ComboBoxItem { Content = t, Tag = b });
        _busy.SelectedItem = _busy.Items.Cast<ComboBoxItem>().First(i => (BusyStatus)i.Tag == e.Busy);
        _category.Items.Add(new ComboBoxItem { Content = L.T("Yok"), Tag = "" });
        foreach (var (name, color) in PimUi.Categories)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = PimUi.Solid(color), RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = L.T(name) });
            _category.Items.Add(new ComboBoxItem { Content = sp, Tag = name });
        }
        _category.SelectedItem = _category.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, e.Category, StringComparison.OrdinalIgnoreCase)) ?? _category.Items[0];
        FillRepeat();
        _hasUntil.IsChecked = e.Repeat?.Until != null;
        ApplyAllDay();
        UpdateMeetingUi();

        // ---- behaviour
        var lastStart = _start.Value;
        _start.Changed += () =>
        {
            // Moving the start keeps the duration (Outlook)
            var newStart = _start.Value; var dur = _end.Value - lastStart;
            if (dur < TimeSpan.Zero) dur = TimeSpan.FromMinutes(30);
            _end.Value = newStart + dur; lastStart = newStart;
            var keep = _repeat.SelectedIndex; FillRepeat(); _repeat.SelectedIndex = Math.Min(keep, _repeat.Items.Count - 1);
            UpdateConflicts();
        };
        _end.Changed += UpdateConflicts;
        _allDay.Checked += (_, _) => ApplyAllDay(); _allDay.Unchecked += (_, _) => ApplyAllDay();
        _repeat.SelectionChanged += (_, _) => UpdateRepeatText();
        _hasUntil.Checked += (_, _) => UpdateRepeatText(); _hasUntil.Unchecked += (_, _) => UpdateRepeatText();
        _until.Changed += UpdateRepeatText;
        _attendees.TextChanged += (_, _) => UpdateMeetingUi();
        PreviewKeyDown += (_, k) =>
        {
            if (k.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Finish(send: false); k.Handled = true; }
            if (k.Key == Key.Escape) { DialogResult = false; k.Handled = true; }
        };
        Loaded += (_, _) => { (_title.Text.Length == 0 ? _title : (Control)_title).Focus(); _title.SelectAll(); UpdateConflicts(); UpdateRepeatText(); };
        if (meeting && isNew) Loaded += (_, _) => _attendees.Focus();
    }

    static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
        var t = PimUi.Text(label, brush: "TextSecondaryBrush"); t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0);
        sp.Children.Add(t); sp.Children.Add(control);
        return sp;
    }

    void ApplyAllDay()
    {
        var all = _allDay.IsChecked == true;
        _start.ShowTime = !all; _end.ShowTime = !all;
        if (all && _busy.SelectedItem is ComboBoxItem { Tag: BusyStatus.Busy } && _isNew) _busy.SelectedItem = _busy.Items.Cast<ComboBoxItem>().First(i => (BusyStatus)i.Tag == BusyStatus.Free);
        if (all && _end.Value.Date < _start.Value.Date) _end.Value = _start.Value.Date;
        UpdateConflicts();
    }

    void UpdateMeetingUi()
    {
        var meeting = Attendees().Count > 0;
        _send.Visibility = meeting && (_e.Organizer.Length == 0 || _isNew || IsOrganizer) ? Visibility.Visible : Visibility.Collapsed;
        _send.Content = _isNew ? L.T("Gönder") : L.T("Güncelleme gönder");
    }

    /// <summary>Set by the caller: the user's own address(es), to know who organises the meeting.</summary>
    public Func<string, bool>? IsOwnAddress { get; set; }
    bool IsOrganizer => _e.Organizer.Length == 0 || IsOwnAddress?.Invoke(Ics.AddressOf(_e.Organizer)) == true;

    List<string> Attendees() => _attendees.Text.Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim()).Where(s => s.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    void AddAttendees()
    {
        var picked = PickAttendees?.Invoke(this);
        if (string.IsNullOrWhiteSpace(picked)) return;
        var current = _attendees.Text.Trim().TrimEnd(';');
        _attendees.Text = current.Length == 0 ? picked : current + "; " + picked;
    }

    record RepeatChoice(string Text, RepeatRule? Rule);

    void FillRepeat()
    {
        var s = _start.Value;
        var nth = (s.Day - 1) / 7 + 1; var isLast = s.AddDays(7).Month != s.Month;
        var choices = new List<RepeatChoice>
        {
            new(L.T("Yinelenmez"), null),
            new(L.T("Her gün"), new RepeatRule { Frequency = RepeatFrequency.Daily }),
            new(L.T("Her iş günü (Pzt–Cum)"), new RepeatRule { Frequency = RepeatFrequency.Weekly, Days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday } }),
            new(L.F("Her hafta ({0})", s.ToString("dddd", L.Culture)), new RepeatRule { Frequency = RepeatFrequency.Weekly, Days = { s.DayOfWeek } }),
            new(L.F("İki haftada bir ({0})", s.ToString("dddd", L.Culture)), new RepeatRule { Frequency = RepeatFrequency.Weekly, Interval = 2, Days = { s.DayOfWeek } }),
            new(L.F("Her ay, {0}. gün", s.Day), new RepeatRule { Frequency = RepeatFrequency.Monthly }),
            new(L.F("Her ay, {0}. {1}", nth > 4 ? 4 : nth, s.ToString("dddd", L.Culture)), new RepeatRule { Frequency = RepeatFrequency.Monthly, WeekOfMonth = Math.Min(nth, 4), Days = { s.DayOfWeek } }),
        };
        if (isLast) choices.Add(new(L.F("Her ay, son {0}", s.ToString("dddd", L.Culture)), new RepeatRule { Frequency = RepeatFrequency.Monthly, WeekOfMonth = -1, Days = { s.DayOfWeek } }));
        choices.Add(new(L.F("Her yıl, {0}", s.ToString("d MMMM", L.Culture)), new RepeatRule { Frequency = RepeatFrequency.Yearly }));
        var existing = _e.Repeat;
        var selected = 0;
        if (existing != null)
        {
            selected = choices.FindIndex(c => c.Rule != null && Ics.WriteRule(Strip(c.Rule)) == Ics.WriteRule(Strip(existing)));
            if (selected < 0) { choices.Add(new(Recurrence.Describe(existing, _e.Start), existing)); selected = choices.Count - 1; }
        }
        _repeat.ItemsSource = choices; _repeat.DisplayMemberPath = nameof(RepeatChoice.Text);
        if (_repeat.SelectedIndex < 0) _repeat.SelectedIndex = selected;
    }

    static RepeatRule Strip(RepeatRule r) => new() { Frequency = r.Frequency, Interval = r.Interval, Days = r.Days, WeekOfMonth = r.WeekOfMonth };

    RepeatRule? CurrentRule()
    {
        if (_repeat.SelectedItem is not RepeatChoice { Rule: { } rule }) return null;
        var r = new RepeatRule { Frequency = rule.Frequency, Interval = rule.Interval, Days = new(rule.Days), WeekOfMonth = rule.WeekOfMonth, Count = rule.Count };
        if (_hasUntil.IsChecked == true) { r.Until = _until.Value.Date; r.Count = null; }
        else if (ReferenceEquals(rule, _e.Repeat)) r.Until = null;
        return r;
    }

    void UpdateRepeatText()
    {
        var rule = CurrentRule();
        _hasUntil.Visibility = _until.Visibility = rule == null ? Visibility.Collapsed : Visibility.Visible;
        _repeatText.Text = rule == null ? "" : Recurrence.Describe(rule, _start.Value);
    }

    void UpdateConflicts()
    {
        if (_allDay.IsChecked == true || !_start.IsValid || !_end.IsValid) { _conflict.Visibility = Visibility.Collapsed; return; }
        var c = _store.Conflicts(_start.Value, _end.Value, _e.Id);
        _conflict.Text = c.Count == 0 ? "" : "⚠ " + L.F("Takviminizdeki başka bir öğeyle çakışıyor: {0}", string.Join(", ", c.Take(3).Select(o => $"{o.Event.Title} ({PimUi.RangeText(o)})")));
        _conflict.Visibility = c.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void Finish(bool send)
    {
        if (!_start.IsValid || !_end.IsValid) { MessageBox.Show(this, L.T("Başlangıç veya bitiş tarihi okunamadı."), Title); return; }
        var allDay = _allDay.IsChecked == true;
        var start = allDay ? _start.Value.Date : _start.Value;
        var end = allDay ? _end.Value.Date.AddDays(1) : _end.Value;
        if (end < start) { MessageBox.Show(this, L.T("Bitiş, başlangıçtan önce olamaz."), Title); return; }
        var attendees = Attendees();
        if (send && attendees.Count == 0) { MessageBox.Show(this, L.T("Davet göndermek için en az bir katılımcı ekleyin."), Title); return; }
        var rule = CurrentRule();
        var timeChanged = !_isNew && (start != _e.Start || end != _e.End || rule?.Frequency != _e.Repeat?.Frequency);
        _e.Title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : L.T("(Konu yok)");
        _e.Location = _location.Text.Trim(); _e.Notes = _notes.Text.TrimEnd();
        _e.Start = start; _e.End = end; _e.AllDay = allDay; _e.Private = _private.IsChecked == true;
        _e.Repeat = rule; if (rule == null) _e.Exceptions.Clear();
        _e.ReminderMinutes = _reminder.SelectedItem is ComboBoxItem { Tag: int m } ? m : 15;
        _e.Busy = _busy.SelectedItem is ComboBoxItem { Tag: BusyStatus b } ? b : BusyStatus.Busy;
        _e.Category = _category.SelectedItem is ComboBoxItem { Tag: string c } ? c : "";
        _e.Attendees = attendees;
        if (send || timeChanged) _e.Sequence++;
        if (send && _e.Response == MeetingResponse.None) _e.Response = MeetingResponse.Organizer;
        SendInvitations = send;
        DialogResult = true;
    }
}

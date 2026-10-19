using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MailStudio.Services;
using MailStudio.Services.Calendar;

namespace MailStudio.Views.Calendar;

/// <summary>Outlook's reminder window: due appointments and tasks with Dismiss / Snooze / Open.</summary>
public sealed class ReminderWindow : Window
{
    readonly CalendarStore _calendar;
    readonly TaskStore _tasks;
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended, MinHeight = 140, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _snooze = new() { MinWidth = 170 };
    readonly TextBlock _head = PimUi.Text("", 14, bold: true);
    public Action<Occurrence>? OpenEvent { get; set; }
    public Action<TaskItem>? OpenTask { get; set; }

    public ReminderWindow(CalendarStore calendar, TaskStore tasks)
    {
        _calendar = calendar; _tasks = tasks;
        Title = L.T("Anımsatıcılar") + " - MailStudio";
        Width = 520; Height = 380; MinWidth = 420; MinHeight = 300; Topmost = true; ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");
        var root = new DockPanel { Margin = new Thickness(14) };
        _head.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_head, Dock.Top); root.Children.Add(_head);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var dismissAll = new Button { Content = L.T("Tümünü kapat"), Padding = new Thickness(12, 4, 12, 4) };
        dismissAll.Click += (_, _) => Dismiss(all: true);
        DockPanel.SetDock(dismissAll, Dock.Left); bottom.Children.Add(dismissAll);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var open = new Button { Content = L.T("Öğeyi aç"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        open.Click += (_, _) => OpenSelected();
        var dismiss = new Button { Content = L.T("Kapat"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Seçili anımsatıcıyı kapat") };
        dismiss.Click += (_, _) => Dismiss(all: false);
        right.Children.Add(open); right.Children.Add(dismiss);
        bottom.Children.Add(right);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var snoozeRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var snoozeBtn = new Button { Content = L.T("Ertele"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        snoozeBtn.SetResourceReference(StyleProperty, "MsPrimaryButtonStyle");
        snoozeBtn.Click += (_, _) => Snooze();
        DockPanel.SetDock(snoozeBtn, Dock.Right); snoozeRow.Children.Add(snoozeBtn);
        var lbl = PimUi.Text(L.T("Yeniden anımsat:"), brush: "TextSecondaryBrush"); lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(lbl, Dock.Left); snoozeRow.Children.Add(lbl);
        snoozeRow.Children.Add(_snooze);
        foreach (var m in new[] { 5, 10, 15, 30, 60, 120, 240, 1440 }) _snooze.Items.Add(new ComboBoxItem { Content = m < 60 ? L.F("{0} dakika", m) : m < 1440 ? L.F("{0} saat", m / 60) : L.T("1 gün"), Tag = m });
        _snooze.Items.Insert(0, new ComboBoxItem { Content = L.T("Başlamadan 5 dakika önce"), Tag = -5 });
        _snooze.SelectedIndex = 1;
        DockPanel.SetDock(snoozeRow, Dock.Bottom); root.Children.Add(snoozeRow);
        root.Children.Add(_list);
        Content = root;
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); if (e.Key == Key.Delete) Dismiss(false); };
        Closing += (_, e) => { if (_force) return; e.Cancel = true; Hide(); }; // reopened by the timer while reminders are due
    }

    bool _force;
    public void ForceClose() { _force = true; Close(); }

    /// <summary>Updates the list; returns how many reminders are due.</summary>
    public int Update(DateTime now)
    {
        var keep = _list.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).Select(Key).ToHashSet();
        var events = _calendar.DueReminders(now);
        var tasks = _tasks.DueReminders(now);
        _list.Items.Clear();
        foreach (var o in events)
        {
            var sp = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };
            sp.Children.Add(PimUi.Text("\U0001F4C5 " + o.Event.Title, 13, bold: true));
            sp.Children.Add(PimUi.Text(PimUi.RangeText(o) + "  ·  " + o.Start.ToString("ddd d MMM", L.Culture) + (o.Event.Location.Length > 0 ? "  ·  " + o.Event.Location : ""), 11.5, brush: "TextSecondaryBrush"));
            var due = PimUi.Text(PimUi.Relative(o.Start, now), 11.5, bold: true, brush: o.Start <= now ? "StatusDangerBrush" : "AccentPrimaryBrush");
            sp.Children.Add(due);
            var item = new ListBoxItem { Content = sp, Tag = o };
            _list.Items.Add(item);
            if (keep.Contains(Key(o))) item.IsSelected = true;
        }
        foreach (var t in tasks)
        {
            var sp = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };
            sp.Children.Add(PimUi.Text("☑ " + t.Title, 13, bold: true));
            sp.Children.Add(PimUi.Text(t.DueDate is { } d ? L.F("Son tarih: {0}", d.ToString("d", L.Culture)) : L.T("Görev"), 11.5, brush: "TextSecondaryBrush"));
            var item = new ListBoxItem { Content = sp, Tag = t };
            _list.Items.Add(item);
            if (keep.Contains(Key(t))) item.IsSelected = true;
        }
        var n = events.Count + tasks.Count;
        _head.Text = n == 1 ? L.T("1 anımsatıcı") : L.F("{0} anımsatıcı", n);
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
        return n;
    }

    static string Key(object? o) => o switch { Occurrence x => "e" + x.Key, TaskItem t => "t" + t.Id, _ => "" };

    IEnumerable<object> Selected(bool all) => (all ? _list.Items.OfType<ListBoxItem>() : _list.SelectedItems.OfType<ListBoxItem>()).Select(i => i.Tag!).ToList();

    void Dismiss(bool all)
    {
        var items = Selected(all).ToList();
        _calendar.Dismiss(items.OfType<Occurrence>());
        foreach (var t in items.OfType<TaskItem>()) _tasks.DismissReminder(t);
        if (Update(DateTime.Now) == 0) Hide();
    }

    void Snooze()
    {
        var minutes = _snooze.SelectedItem is ComboBoxItem { Tag: int m } ? m : 5;
        var now = DateTime.Now;
        var items = Selected(false).ToList();
        foreach (var o in items.OfType<Occurrence>())
            _calendar.Snooze(new[] { o }, minutes < 0 ? (o.Start.AddMinutes(minutes) > now ? o.Start.AddMinutes(minutes) : now.AddMinutes(5)) : now.AddMinutes(minutes));
        foreach (var t in items.OfType<TaskItem>()) _tasks.SnoozeReminder(t, now.AddMinutes(minutes < 0 ? 5 : minutes));
        if (Update(now) == 0) Hide();
    }

    void OpenSelected()
    {
        switch (Selected(false).FirstOrDefault())
        {
            case Occurrence o: OpenEvent?.Invoke(o); break;
            case TaskItem t: OpenTask?.Invoke(t); break;
        }
    }
}

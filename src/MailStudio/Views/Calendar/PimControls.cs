using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views.Calendar;

/// <summary>Themed month navigator (Outlook's date navigator): click a day to select it; days with events are bold.</summary>
public sealed class MiniMonth : UserControl
{
    DateTime _month;
    DateTime _selected = DateTime.Today;
    readonly TextBlock _title = PimUi.Text("", 13, bold: true);
    readonly UniformGrid _days = new() { Columns = 7, Rows = 7 };
    public Func<DateTime, bool>? HasEvents { get; set; }
    /// <summary>Highlighted range (the visible week / work week).</summary>
    public (DateTime From, DateTime To)? Range { get; set; }
    public event Action<DateTime>? DatePicked;

    public DateTime Selected
    {
        get => _selected;
        set { _selected = value.Date; _month = new DateTime(value.Year, value.Month, 1); Render(); }
    }

    public MiniMonth()
    {
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var root = new DockPanel { Margin = new Thickness(6, 4, 6, 4) };
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        Button Arrow(string glyph, int delta, string tip)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Width = 26, Height = 24, Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "IconButton");
            b.Click += (_, _) => { _month = _month.AddMonths(delta); Render(); };
            return b;
        }
        var next = Arrow("", 1, L.T("Sonraki ay")); DockPanel.SetDock(next, Dock.Right); head.Children.Add(next);
        var prev = Arrow("", -1, L.T("Önceki ay")); DockPanel.SetDock(prev, Dock.Right); head.Children.Add(prev);
        _title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(_title);
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        root.Children.Add(_days);
        Content = root;
        Loaded += (_, _) => Render();
    }

    public void Render()
    {
        _title.Text = _month.ToString("MMMM yyyy", L.Culture);
        _days.Children.Clear();
        var first = MailStudio.Services.Calendar.Recurrence.WeekStart(_month);
        for (var i = 0; i < 7; i++)
        {
            var abbr = L.Culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((i + 1) % 7));
            var dn = PimUi.Text(abbr.Length > 2 ? abbr[..2] : abbr, 11, brush: "TextSecondaryBrush");
            dn.HorizontalAlignment = HorizontalAlignment.Center;
            _days.Children.Add(dn);
        }
        for (var i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            var inMonth = day.Month == _month.Month;
            var b = new Button
            {
                Content = day.Day.ToString(CultureInfo.InvariantCulture), Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, Height = 24, FontSize = 11.5,
                ToolTip = day.ToString("D", L.Culture), Tag = day, Margin = new Thickness(0.5)
            };
            b.SetResourceReference(StyleProperty, "SubtleButton");
            if (!inMonth) b.SetResourceReference(ForegroundProperty, "TextMutedBrush");
            if (HasEvents?.Invoke(day) == true) b.FontWeight = FontWeights.Bold;
            if (Range is { } r && day >= r.From.Date && day < r.To.Date) b.SetResourceReference(BackgroundProperty, "AccentSubtleBrush");
            if (day == _selected) { b.SetResourceReference(BackgroundProperty, "AccentSubtleBrush"); b.SetResourceReference(BorderBrushProperty, "AccentPrimaryBrush"); }
            if (day == DateTime.Today) { b.SetResourceReference(BackgroundProperty, "AccentFillBrush"); b.SetResourceReference(ForegroundProperty, "TextOnAccentBrush"); }
            b.Click += (_, _) => { _selected = day; DatePicked?.Invoke(day); Render(); };
            _days.Children.Add(b);
        }
    }
}

/// <summary>Date + optional time input: a text box (typed dates in the UI culture), a calendar popup and a time list.</summary>
public sealed class DateTimeField : StackPanel
{
    readonly TextBox _date = new() { Width = 120, VerticalContentAlignment = VerticalAlignment.Center };
    readonly ComboBox _time = new() { Width = 86, IsEditable = true, Margin = new Thickness(6, 0, 0, 0), MaxDropDownHeight = 260 };
    readonly Popup _popup = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };
    readonly MiniMonth _month = new() { Width = 230 };
    public event Action? Changed;
    bool _suppress;

    public DateTimeField(DateTime value, bool showTime = true)
    {
        Orientation = Orientation.Horizontal;
        Children.Add(_date);
        var pick = new Button { Content = "", Width = 30, Margin = new Thickness(2, 0, 0, 0), ToolTip = L.T("Takvimden seç") };
        pick.SetResourceReference(StyleProperty, "IconButton");
        Children.Add(pick);
        var popupBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Child = _month };
        popupBorder.SetResourceReference(Border.BackgroundProperty, "BgPopupBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        _popup.Child = popupBorder; _popup.PlacementTarget = _date;
        pick.Click += (_, _) => { _month.Selected = Value.Date; _popup.IsOpen = true; };
        _month.DatePicked += d => { _popup.IsOpen = false; SetDate(d); };
        for (var m = 0; m < 24 * 60; m += 30) _time.Items.Add(new DateTime(2000, 1, 1).AddMinutes(m).ToString("t", L.Culture));
        Children.Add(_time);
        _time.Visibility = showTime ? Visibility.Visible : Visibility.Collapsed;
        Value = value;
        _date.LostFocus += (_, _) => { if (!_suppress) Changed?.Invoke(); };
        _time.SelectionChanged += (_, _) => { if (!_suppress) Dispatcher.BeginInvoke(() => Changed?.Invoke()); };
        _time.LostFocus += (_, _) => { if (!_suppress) Changed?.Invoke(); };
    }

    public bool ShowTime { get => _time.Visibility == Visibility.Visible; set => _time.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    void SetDate(DateTime d) { _date.Text = d.ToString("d", L.Culture); Changed?.Invoke(); }

    public DateTime Value
    {
        get
        {
            var date = DateTime.TryParse(_date.Text, L.Culture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d.Date : DateTime.Today;
            if (!ShowTime) return date;
            return TryTime(_time.Text, out var t) ? date + t : date + new TimeSpan(9, 0, 0);
        }
        set
        {
            _suppress = true;
            _date.Text = value.ToString("d", L.Culture);
            _time.Text = value.ToString("t", L.Culture);
            _suppress = false;
        }
    }

    public bool IsValid => DateTime.TryParse(_date.Text, L.Culture, DateTimeStyles.AllowWhiteSpaces, out _) && (!ShowTime || TryTime(_time.Text, out _));

    /// <summary>"9", "930", "09:30", "9.30", "14:05" and the culture's short time.</summary>
    public static bool TryTime(string? text, out TimeSpan t)
    {
        t = default;
        var s = (text ?? "").Trim();
        if (DateTime.TryParseExact(s, new[] { "t", "H:mm", "HH:mm", "H.mm", "HH.mm", "H", "HH", "Hmm", "HHmm" }, L.Culture, DateTimeStyles.None, out var dt) ||
            DateTime.TryParse(s, L.Culture, DateTimeStyles.NoCurrentDateDefault, out dt))
        { t = dt.TimeOfDay; return true; }
        return false;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MailStudio.Services;
using MailStudio.Services.Calendar;

namespace MailStudio.Views.Calendar;

/// <summary>Shared look of the calendar and task views (Outlook-like colour categories, ribbon-style buttons).</summary>
public static class PimUi
{
    /// <summary>Outlook colour categories: stored name (Turkish key) → colour.</summary>
    public static readonly (string Name, Color Color)[] Categories =
    {
        ("Kırmızı", Color.FromRgb(0xD1, 0x34, 0x38)), ("Turuncu", Color.FromRgb(0xF7, 0x63, 0x0C)), ("Sarı", Color.FromRgb(0xEA, 0xA3, 0x00)),
        ("Yeşil", Color.FromRgb(0x10, 0x7C, 0x10)), ("Mavi", Color.FromRgb(0x0F, 0x6C, 0xBD)), ("Mor", Color.FromRgb(0x87, 0x64, 0xB8)),
    };

    public static Color CategoryColor(string? name)
    {
        foreach (var (n, c) in Categories) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return c;
        return ThemeManager.Color("AccentPrimaryBrush") is { A: > 0 } accent ? accent : Color.FromRgb(0x0F, 0x6C, 0xBD);
    }

    public static Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public static Brush Tint(Color c, bool strong = false) => Solid(Color.FromArgb((byte)(ThemeManager.IsDark ? (strong ? 0x80 : 0x55) : (strong ? 0x55 : 0x2E)), c.R, c.G, c.B));

    public static Button Cmd(string glyph, string text, RoutedEventHandler click, string? tooltip = null, bool primary = false)
    {
        var b = new Button { Content = text, Tag = glyph, ToolTip = tooltip ?? text, Margin = new Thickness(1, 0, 1, 0) };
        b.SetResourceReference(FrameworkElement.StyleProperty, primary ? "RibbonPrimaryCmd" : "RibbonCmd");
        b.Click += click;
        return b;
    }

    public static RadioButton Segment(string text, string group, RoutedEventHandler click)
    {
        var b = new RadioButton { Content = text, GroupName = group };
        b.SetResourceReference(FrameworkElement.StyleProperty, "SegmentRadio");
        b.Checked += click;
        return b;
    }

    public static HeaderedContentControl Group(string header, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) panel.Children.Add(c);
        var g = new HeaderedContentControl { Header = header, Content = panel };
        g.SetResourceReference(FrameworkElement.StyleProperty, "RibbonGroup");
        return g;
    }

    public static TextBlock Text(string s, double size = 12.5, bool bold = false, string brush = "TextPrimaryBrush")
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static string TimeText(DateTime t) => t.ToString("t", L.Culture);

    public static string RangeText(Occurrence o)
    {
        if (o.Event.AllDay)
        {
            var last = o.End.AddDays(-1);
            return last.Date > o.Start.Date ? $"{o.Start.ToString("d MMM", L.Culture)} – {last.ToString("d MMM", L.Culture)}" : L.T("Tüm gün");
        }
        return o.End.Date > o.Start.Date
            ? $"{o.Start.ToString("g", L.Culture)} – {o.End.ToString("g", L.Culture)}"
            : $"{TimeText(o.Start)} – {TimeText(o.End)}";
    }

    public static CultureInfo Culture => L.Culture;

    /// <summary>Reminder choices (minutes; -1 none) as Outlook offers them.</summary>
    public static readonly int[] ReminderChoices = { -1, 0, 5, 10, 15, 30, 60, 120, 240, 720, 1440, 2880, 10080 };

    public static string ReminderText(int minutes) => minutes switch
    {
        < 0 => L.T("Yok"),
        0 => L.T("Başlangıçta"),
        < 60 => L.F("{0} dakika önce", minutes),
        < 1440 => L.F("{0} saat önce", minutes / 60),
        < 10080 => L.F("{0} gün önce", minutes / 1440),
        _ => L.F("{0} hafta önce", minutes / 10080)
    };

    public static string Relative(DateTime start, DateTime now)
    {
        var d = start - now;
        if (d.TotalMinutes <= 0) return d.TotalMinutes > -1 ? L.T("Şimdi") : L.F("{0} önce başladı", Span(-d));
        return L.F("{0} sonra", Span(d));
    }

    static string Span(TimeSpan d) => d.TotalMinutes < 60 ? L.F("{0} dk", (int)Math.Ceiling(d.TotalMinutes))
        : d.TotalHours < 24 ? L.F("{0} sa", (int)Math.Round(d.TotalHours)) : L.F("{0} gün", (int)Math.Round(d.TotalDays));
}

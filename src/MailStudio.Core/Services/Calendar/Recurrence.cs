namespace MailStudio.Services.Calendar;

/// <summary>Expands repeating events into occurrences (daily, weekly on days, monthly by date or "2nd Tuesday", yearly).</summary>
public static class Recurrence
{
    const int MaxOccurrences = 5000;

    public static IEnumerable<Occurrence> Expand(CalendarEvent e, DateTime from, DateTime to)
    {
        var duration = e.Duration;
        if (e.Repeat is not { Frequency: not RepeatFrequency.None } rule)
        {
            var single = new Occurrence(e, e.Start, e.End);
            if (single.Overlaps(from, to)) yield return single;
            yield break;
        }
        var excluded = new HashSet<DateTime>(e.Exceptions.Select(x => Trim(x)));
        var n = 0;
        foreach (var start in Starts(e.Start, rule))
        {
            if (rule.Until is { } until && start.Date > until.Date) yield break;
            if (rule.Count is { } count && n >= count) yield break;
            if (++n > MaxOccurrences || start >= to) yield break;
            if (excluded.Contains(Trim(start))) continue;
            var occurrence = new Occurrence(e, start, start + duration);
            if (occurrence.Overlaps(from, to)) yield return occurrence;
        }
    }

    static DateTime Trim(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0);

    /// <summary>Every occurrence start in order, beginning with the series start.</summary>
    public static IEnumerable<DateTime> Starts(DateTime first, RepeatRule rule)
    {
        var interval = Math.Max(1, rule.Interval);
        var time = first.TimeOfDay;
        switch (rule.Frequency)
        {
            case RepeatFrequency.Daily:
                for (var d = first; ; d = d.AddDays(interval)) yield return d;
            case RepeatFrequency.Weekly:
            {
                var days = rule.Days.Count > 0 ? rule.Days.ToHashSet() : new HashSet<DayOfWeek> { first.DayOfWeek };
                var weekStart = WeekStart(first.Date);
                for (var week = 0; ; week += interval)
                {
                    var monday = weekStart.AddDays(7 * week);
                    for (var i = 0; i < 7; i++)
                    {
                        var day = monday.AddDays(i);
                        if (day < first.Date || !days.Contains(day.DayOfWeek)) continue;
                        yield return day + time;
                    }
                }
            }
            case RepeatFrequency.Monthly:
                for (var m = 0; ; m += interval)
                {
                    var month = new DateTime(first.Year, first.Month, 1).AddMonths(m);
                    var day = DayInMonth(month, first, rule);
                    if (day is { } d && d + time >= first) yield return d + time;
                    if (month.Year > 9000) yield break;
                }
            case RepeatFrequency.Yearly:
                for (var y = 0; ; y += interval)
                {
                    var month = new DateTime(first.Year + y, first.Month, 1);
                    var day = DayInMonth(month, first, rule);
                    if (day is { } d && d + time >= first) yield return d + time;
                    if (month.Year > 9000) yield break;
                }
            default:
                yield return first;
                yield break;
        }
    }

    /// <summary>Monday-based week start (Turkish/European calendars).</summary>
    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    static DateTime? DayInMonth(DateTime month, DateTime first, RepeatRule rule)
    {
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        if (rule.WeekOfMonth is { } nth)
        {
            var dow = rule.Days.Count > 0 ? rule.Days[0] : first.DayOfWeek;
            if (nth > 0)
            {
                var d = month;
                while (d.DayOfWeek != dow) d = d.AddDays(1);
                d = d.AddDays(7 * (nth - 1));
                return d.Month == month.Month ? d : null;
            }
            var last = new DateTime(month.Year, month.Month, daysInMonth);
            while (last.DayOfWeek != dow) last = last.AddDays(-1);
            return last;
        }
        // Same day number; months without it (31st, Feb 29) are skipped like RFC 5545
        return first.Day <= daysInMonth ? new DateTime(month.Year, month.Month, first.Day) : null;
    }

    /// <summary>Human readable rule ("Her hafta Pzt, Çar") in the UI language.</summary>
    public static string Describe(RepeatRule? rule, DateTime start)
    {
        if (rule is not { Frequency: not RepeatFrequency.None }) return L.T("Yinelenmez");
        if (rule.IsWorkdays) return L.T("Her iş günü");
        var every = rule.Interval > 1;
        string text = rule.Frequency switch
        {
            RepeatFrequency.Daily => every ? L.F("Her {0} günde bir", rule.Interval) : L.T("Her gün"),
            RepeatFrequency.Weekly => (every ? L.F("Her {0} haftada bir", rule.Interval) : L.T("Her hafta")) + " " +
                string.Join(", ", (rule.Days.Count > 0 ? rule.Days : new() { start.DayOfWeek }).OrderBy(d => ((int)d + 6) % 7).Select(d => L.Culture.DateTimeFormat.GetAbbreviatedDayName(d))),
            RepeatFrequency.Monthly => (every ? L.F("Her {0} ayda bir", rule.Interval) : L.T("Her ay")) + " " + MonthPart(rule, start),
            RepeatFrequency.Yearly => L.T("Her yıl") + " " + start.ToString("d MMMM", L.Culture),
            _ => ""
        };
        if (rule.Until is { } until) text += " · " + L.F("{0} tarihine kadar", until.ToString("d", L.Culture));
        else if (rule.Count is { } c) text += " · " + L.F("{0} kez", c);
        return text;
    }

    static string MonthPart(RepeatRule rule, DateTime start)
    {
        if (rule.WeekOfMonth is not { } nth) return L.F("{0}. gün", start.Day);
        var day = L.Culture.DateTimeFormat.GetDayName(rule.Days.Count > 0 ? rule.Days[0] : start.DayOfWeek);
        return nth < 0 ? L.F("son {0}", day) : L.F("{0}. {1}", nth, day);
    }
}

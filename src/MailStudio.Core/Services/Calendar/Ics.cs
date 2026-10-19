using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MailStudio.Services.Calendar;

/// <summary>A parsed iCalendar file: METHOD (PUBLISH, REQUEST = invitation, CANCEL, REPLY) and its events.</summary>
public sealed class IcsCalendar
{
    public string Method { get; set; } = "";
    public List<CalendarEvent> Events { get; } = new();
    /// <summary>For METHOD:REPLY: attendee address → PARTSTAT.</summary>
    public Dictionary<string, string> Replies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsInvitation => Method.Equals("REQUEST", StringComparison.OrdinalIgnoreCase);
    public bool IsCancel => Method.Equals("CANCEL", StringComparison.OrdinalIgnoreCase);
    public bool IsReply => Method.Equals("REPLY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// iCalendar (RFC 5545) reader/writer for what Outlook, Exchange, Google Calendar and Teams/Zoom invitations contain:
/// time zones (IANA, Windows and Outlook's own VTIMEZONE names), all-day events, RRULE/EXDATE, organizer/attendees, alarms.
/// </summary>
public static class Ics
{
    sealed record Prop(string Name, Dictionary<string, string> Params, string Value);

    // ------------------------------------------------------------------ reading

    public static IcsCalendar Parse(string text)
    {
        var cal = new IcsCalendar();
        var lines = Unfold(text);
        var zones = ReadZones(lines);
        var overrides = new List<(CalendarEvent Override, DateTime RecurrenceId)>();
        List<Prop>? ev = null; var depth = 0; var inAlarm = false; int? alarmMinutes = null;
        foreach (var raw in lines)
        {
            var p = ParseLine(raw);
            if (p == null) continue;
            if (p.Name == "BEGIN")
            {
                if (p.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && ev == null) { ev = new(); alarmMinutes = null; depth = 0; continue; }
                if (ev != null) { depth++; if (p.Value.Equals("VALARM", StringComparison.OrdinalIgnoreCase)) inAlarm = true; }
                continue;
            }
            if (p.Name == "END")
            {
                if (ev != null && depth == 0 && p.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    var (e, recurrenceId) = ToEvent(ev, zones, alarmMinutes);
                    if (e != null) { if (recurrenceId is { } rid) overrides.Add((e, rid)); else cal.Events.Add(e); }
                    if (cal.Method.Equals("REPLY", StringComparison.OrdinalIgnoreCase))
                        foreach (var a in ev.Where(x => x.Name == "ATTENDEE"))
                            cal.Replies[Mailto(a.Value)] = a.Params.GetValueOrDefault("PARTSTAT", "");
                    ev = null;
                }
                else if (ev != null) { depth--; if (p.Value.Equals("VALARM", StringComparison.OrdinalIgnoreCase)) inAlarm = false; }
                continue;
            }
            if (ev == null) { if (p.Name == "METHOD") cal.Method = p.Value.Trim().ToUpperInvariant(); continue; }
            if (inAlarm)
            {
                if (p.Name == "TRIGGER" && !p.Params.ContainsKey("VALUE") && TryDuration(p.Value, out var t) && t <= TimeSpan.Zero)
                    alarmMinutes = alarmMinutes == null ? (int)Math.Round(-t.TotalMinutes) : Math.Max(alarmMinutes.Value, (int)Math.Round(-t.TotalMinutes));
                continue;
            }
            if (depth == 0) ev.Add(p);
        }
        // A changed single occurrence (RECURRENCE-ID): hide it in the series and keep the changed copy as its own event
        foreach (var (o, rid) in overrides)
        {
            var master = cal.Events.FirstOrDefault(x => x.Uid == o.Uid && x.Repeat != null);
            if (master != null) { master.Exceptions.Add(rid); o.Uid = o.Uid + "#" + rid.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture); }
            cal.Events.Add(o);
        }
        return cal;
    }

    static List<string> Unfold(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && result.Count > 0) result[^1] += line[1..];
            else if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    static Prop? ParseLine(string line)
    {
        // NAME;PARAM=a;PARAM2="b:c":value  — colons inside quoted parameters are not the separator
        var inQuote = false; var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ':' && !inQuote) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var head = line[..colon]; var value = line[(colon + 1)..];
        var parts = SplitParams(head);
        var name = parts[0].Trim().ToUpperInvariant();
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) ps[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
        }
        return new Prop(name, ps, value);
    }

    static List<string> SplitParams(string head)
    {
        var list = new List<string>(); var sb = new StringBuilder(); var q = false;
        foreach (var ch in head)
        {
            if (ch == '"') q = !q;
            if (ch == ';' && !q) { list.Add(sb.ToString()); sb.Clear(); } else sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list;
    }

    /// <summary>VTIMEZONE blocks → (standard offset, daylight offset, daylight start month/week/day rules).</summary>
    /// <summary>Offsets of a VTIMEZONE block.</summary>
    public sealed class Zone
    {
        public TimeSpan Standard; public TimeSpan? Daylight;
        public (int Month, int Nth, DayOfWeek Day, TimeSpan At)? ToDaylight, ToStandard;
        public TimeSpan OffsetAt(DateTime local)
        {
            if (Daylight == null || ToDaylight == null || ToStandard == null) return Standard;
            var y = local.Year;
            var dst = Transition(y, ToDaylight.Value); var std = Transition(y, ToStandard.Value);
            var inDst = dst < std ? local >= dst && local < std : local >= dst || local < std; // southern hemisphere
            return inDst ? Daylight.Value : Standard;
        }
        static DateTime Transition(int year, (int Month, int Nth, DayOfWeek Day, TimeSpan At) r)
        {
            DateTime d;
            if (r.Nth > 0) { d = new DateTime(year, r.Month, 1); while (d.DayOfWeek != r.Day) d = d.AddDays(1); d = d.AddDays(7 * (r.Nth - 1)); if (d.Month != r.Month) d = d.AddDays(-7); }
            else { d = new DateTime(year, r.Month, DateTime.DaysInMonth(year, r.Month)); while (d.DayOfWeek != r.Day) d = d.AddDays(-1); }
            return d + r.At;
        }
    }

    static Dictionary<string, Zone> ReadZones(List<string> lines)
    {
        var zones = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
        string? tzid = null; string? sub = null; Zone? zone = null; TimeSpan? offset = null; string? rrule = null; DateTime? dtstart = null;
        foreach (var raw in lines)
        {
            var p = ParseLine(raw); if (p == null) continue;
            if (p.Name == "BEGIN" && p.Value.Equals("VTIMEZONE", StringComparison.OrdinalIgnoreCase)) { zone = new Zone(); tzid = null; continue; }
            if (zone == null) continue;
            if (p.Name == "TZID" && sub == null) tzid = p.Value.Trim();
            else if (p.Name == "BEGIN") { sub = p.Value.ToUpperInvariant(); offset = null; rrule = null; dtstart = null; }
            else if (p.Name == "TZOFFSETTO" && sub != null) offset = ParseOffset(p.Value);
            else if (p.Name == "RRULE" && sub != null) rrule = p.Value;
            else if (p.Name == "DTSTART" && sub != null && DateTime.TryParseExact(p.Value.Trim(), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ds)) dtstart = ds;
            else if (p.Name == "END" && sub != null && p.Value.Equals(sub, StringComparison.OrdinalIgnoreCase))
            {
                var rule = TransitionRule(rrule, dtstart);
                if (sub == "STANDARD" && offset != null) { zone.Standard = offset.Value; zone.ToStandard = rule; }
                if (sub == "DAYLIGHT" && offset != null) { zone.Daylight = offset.Value; zone.ToDaylight = rule; }
                sub = null;
            }
            else if (p.Name == "END" && p.Value.Equals("VTIMEZONE", StringComparison.OrdinalIgnoreCase))
            {
                if (tzid != null) zones[tzid] = zone;
                zone = null;
            }
        }
        return zones;
    }

    static (int, int, DayOfWeek, TimeSpan)? TransitionRule(string? rrule, DateTime? start)
    {
        if (rrule == null || start == null) return null;
        var parts = RuleParts(rrule);
        if (!parts.TryGetValue("BYMONTH", out var m) || !int.TryParse(m, out var month)) return null;
        if (!parts.TryGetValue("BYDAY", out var bd)) return null;
        var mm = Regex.Match(bd, @"^([+-]?\d)?([A-Z]{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!mm.Success || ParseDay(mm.Groups[2].Value) is not { } day) return null;
        var nth = mm.Groups[1].Success ? int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        return (month, nth < 0 ? -1 : nth, day, start.Value.TimeOfDay);
    }

    static TimeSpan? ParseOffset(string v)
    {
        var m = Regex.Match(v.Trim(), @"^([+-])(\d{2})(\d{2})(\d{2})?$");
        if (!m.Success) return null;
        var t = new TimeSpan(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0);
        return m.Groups[1].Value == "-" ? -t : t;
    }

    static (CalendarEvent?, DateTime?) ToEvent(List<Prop> props, Dictionary<string, Zone> zones, int? alarmMinutes)
    {
        Prop? Get(string n) => props.FirstOrDefault(p => p.Name == n);
        var start = Get("DTSTART"); if (start == null) return (null, null);
        var e = new CalendarEvent
        {
            Uid = Get("UID")?.Value.Trim() is { Length: > 0 } uid ? uid : Guid.NewGuid().ToString("N"),
            Title = Text(Get("SUMMARY")?.Value),
            Location = Text(Get("LOCATION")?.Value),
            Notes = Text(Get("DESCRIPTION")?.Value),
            Sequence = int.TryParse(Get("SEQUENCE")?.Value, out var seq) ? seq : 0,
            ReminderMinutes = alarmMinutes ?? -1,
            Cancelled = Get("STATUS")?.Value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true,
            Private = Get("CLASS")?.Value.Trim() is "PRIVATE" or "CONFIDENTIAL",
            Category = Get("CATEGORIES")?.Value.Split(',')[0].Trim() ?? "",
        };
        var (s, allDay) = ParseTime(start, zones);
        e.Start = s; e.AllDay = allDay || Get("X-MICROSOFT-CDO-ALLDAYEVENT")?.Value.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase) == true;
        if (Get("DTEND") is { } end) e.End = ParseTime(end, zones).Time;
        else if (Get("DURATION") is { } dur && TryDuration(dur.Value, out var d)) e.End = e.Start + d;
        else e.End = e.AllDay ? e.Start.AddDays(1) : e.Start;
        if (e.End < e.Start) e.End = e.Start;
        if (e.AllDay) { e.Start = e.Start.Date; e.End = e.End.Date <= e.Start ? e.Start.AddDays(1) : e.End.Date; }
        e.Busy = (Get("X-MICROSOFT-CDO-BUSYSTATUS")?.Value.Trim().ToUpperInvariant(), Get("TRANSP")?.Value.Trim().ToUpperInvariant()) switch
        {
            ("FREE", _) or (null, "TRANSPARENT") => BusyStatus.Free,
            ("TENTATIVE", _) => BusyStatus.Tentative,
            ("OOF", _) => BusyStatus.OutOfOffice,
            _ => BusyStatus.Busy
        };
        if (Get("ORGANIZER") is { } org) e.Organizer = Person(org);
        foreach (var a in props.Where(p => p.Name == "ATTENDEE"))
        {
            var person = Person(a);
            if (person.Length > 0 && !e.Attendees.Contains(person, StringComparer.OrdinalIgnoreCase)) e.Attendees.Add(person);
        }
        if (Get("RRULE") is { } rr) e.Repeat = ParseRule(rr.Value, zones);
        foreach (var ex in props.Where(p => p.Name == "EXDATE"))
            foreach (var v in ex.Value.Split(','))
                e.Exceptions.Add(ParseTime(new Prop("EXDATE", ex.Params, v), zones).Time);
        DateTime? recurrenceId = Get("RECURRENCE-ID") is { } rid ? ParseTime(rid, zones).Time : null;
        if (Get("LAST-MODIFIED") is { } lm) e.Modified = ParseTime(lm, zones).Time;
        return (e, recurrenceId);
    }

    static string Person(Prop p)
    {
        var addr = Mailto(p.Value);
        var cn = p.Params.GetValueOrDefault("CN", "").Trim();
        if (addr.Length == 0) return cn;
        return cn.Length > 0 && !cn.Equals(addr, StringComparison.OrdinalIgnoreCase) ? $"{cn} <{addr}>" : addr;
    }

    static string Mailto(string v)
    {
        v = v.Trim();
        return v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? v[7..].Trim() : v.Contains('@') ? v : "";
    }

    static string Text(string? v) => v == null ? "" :
        Regex.Replace(v, @"\\([nN,;\\])", m => m.Groups[1].Value switch { "n" or "N" => "\n", var c => c }).Trim();

    static (DateTime Time, bool AllDay) ParseTime(Prop p, Dictionary<string, Zone> zones)
    {
        var v = p.Value.Trim();
        if (p.Params.GetValueOrDefault("VALUE", "").Equals("DATE", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(v, @"^\d{8}$"))
            return (DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today, true);
        var utc = v.EndsWith('Z') || v.EndsWith('z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z', 'z'), new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return (DateTime.Today, false);
        if (utc) return (DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().AsLocalWall(), false);
        if (p.Params.TryGetValue("TZID", out var tzid) && tzid.Length > 0)
        {
            var utcTime = ToUtc(t, tzid, zones);
            if (utcTime != null) return (utcTime.Value.ToLocalTime().AsLocalWall(), false);
        }
        return (t, false); // floating time = local
    }

    /// <summary>Wall time in zone <paramref name="tzid"/> → UTC. IANA, Windows and Outlook VTIMEZONE names.</summary>
    public static DateTime? ToUtc(DateTime wall, string tzid, IReadOnlyDictionary<string, Zone>? zones = null)
    {
        if (FindZone(tzid) is { } tz)
        {
            try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), tz); }
            catch (ArgumentException) { return DateTime.SpecifyKind(wall - tz.GetUtcOffset(wall), DateTimeKind.Utc); } // invalid (skipped) DST hour
        }
        if (zones != null && zones.TryGetValue(tzid, out var z)) return DateTime.SpecifyKind(wall - z.OffsetAt(wall), DateTimeKind.Utc);
        return null;
    }

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Turkey Standard Time"] = "Europe/Istanbul", ["GTB Standard Time"] = "Europe/Bucharest", ["W. Europe Standard Time"] = "Europe/Berlin",
        ["GMT Standard Time"] = "Europe/London", ["Romance Standard Time"] = "Europe/Paris", ["Central Europe Standard Time"] = "Europe/Budapest",
        ["Eastern Standard Time"] = "America/New_York", ["Pacific Standard Time"] = "America/Los_Angeles", ["Central Standard Time"] = "America/Chicago",
        ["Russian Standard Time"] = "Europe/Moscow", ["Arabian Standard Time"] = "Asia/Dubai", ["China Standard Time"] = "Asia/Shanghai",
        ["Tokyo Standard Time"] = "Asia/Tokyo", ["India Standard Time"] = "Asia/Kolkata", ["UTC"] = "Etc/UTC", ["GMT"] = "Etc/UTC",
    };

    static TimeZoneInfo? FindZone(string tzid)
    {
        var id = tzid.Trim().Trim('"');
        if (id.StartsWith('/')) id = id.TrimStart('/'); // some servers prefix "/mozilla.org/..." style ids
        foreach (var candidate in new[] { id, Aliases.GetValueOrDefault(id) ?? "" }.Where(x => x.Length > 0))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        }
        // Outlook names like "(UTC+03:00) Istanbul" or "Istanbul"
        if (id.Contains("Istanbul", StringComparison.OrdinalIgnoreCase)) return FindZone("Europe/Istanbul");
        return null;
    }

    static bool TryDuration(string v, out TimeSpan d)
    {
        d = TimeSpan.Zero;
        var m = Regex.Match(v.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return false;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        d = new TimeSpan(G(2) * 7 + G(3), G(4), G(5), G(6));
        if (m.Groups[1].Value == "-") d = -d;
        return true;
    }

    static Dictionary<string, string> RuleParts(string rule) =>
        rule.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .GroupBy(x => x[0].Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());

    static DayOfWeek? ParseDay(string s) => s.ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null
    };

    static string DayCode(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "MO", DayOfWeek.Tuesday => "TU", DayOfWeek.Wednesday => "WE", DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR", DayOfWeek.Saturday => "SA", _ => "SU"
    };

    public static RepeatRule? ParseRule(string rrule, Dictionary<string, Zone>? zones = null)
    {
        var parts = RuleParts(rrule);
        var freq = parts.GetValueOrDefault("FREQ", "") switch
        {
            "DAILY" => RepeatFrequency.Daily, "WEEKLY" => RepeatFrequency.Weekly, "MONTHLY" => RepeatFrequency.Monthly, "YEARLY" => RepeatFrequency.Yearly,
            _ => RepeatFrequency.None
        };
        if (freq == RepeatFrequency.None) return null;
        var rule = new RepeatRule { Frequency = freq, Interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1 };
        if (int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) && c > 0) rule.Count = c;
        if (parts.TryGetValue("UNTIL", out var until))
            rule.Until = ParseTime(new Prop("UNTIL", new(), until), zones ?? new()).Time;
        if (parts.TryGetValue("BYDAY", out var byday))
        {
            foreach (var token in byday.Split(','))
            {
                var m = Regex.Match(token.Trim(), @"^([+-]?\d)?([A-Za-z]{2})$");
                if (!m.Success || ParseDay(m.Groups[2].Value) is not { } day) continue;
                rule.Days.Add(day);
                if (m.Groups[1].Success && freq is RepeatFrequency.Monthly or RepeatFrequency.Yearly)
                    rule.WeekOfMonth = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var n && n < 0 ? -1 : n;
            }
            // MONTHLY;BYDAY=TU;BYSETPOS=2 (Outlook writes this form too)
            if (freq is RepeatFrequency.Monthly or RepeatFrequency.Yearly && rule.WeekOfMonth == null && int.TryParse(parts.GetValueOrDefault("BYSETPOS"), out var pos) && rule.Days.Count == 1)
                rule.WeekOfMonth = pos < 0 ? -1 : pos;
            if (freq == RepeatFrequency.Daily && rule.Days.Count > 0) rule.Frequency = RepeatFrequency.Weekly; // "every weekday" written as DAILY;BYDAY=MO..FR
        }
        return rule;
    }

    // ------------------------------------------------------------------ writing

    public static string WriteCalendar(IEnumerable<CalendarEvent> events, string method = "PUBLISH", string? organizer = null, string? replyFrom = null, string? partStat = null)
    {
        var sb = new StringBuilder();
        void W(string s) => Fold(sb, s);
        W("BEGIN:VCALENDAR"); W("PRODID:-//TRMachinist//MailStudio//TR"); W("VERSION:2.0"); W("CALSCALE:GREGORIAN"); W("METHOD:" + method);
        foreach (var e in events)
        {
            W("BEGIN:VEVENT");
            W("UID:" + e.Uid);
            W("SEQUENCE:" + e.Sequence.ToString(CultureInfo.InvariantCulture));
            W("DTSTAMP:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            if (e.AllDay) { W("DTSTART;VALUE=DATE:" + e.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture)); W("DTEND;VALUE=DATE:" + (e.End > e.Start ? e.End : e.Start.AddDays(1)).ToString("yyyyMMdd", CultureInfo.InvariantCulture)); W("X-MICROSOFT-CDO-ALLDAYEVENT:TRUE"); }
            else { W("DTSTART:" + Utc(e.Start)); W("DTEND:" + Utc(e.End)); }
            W("SUMMARY:" + Escape(e.Title));
            if (e.Location.Length > 0) W("LOCATION:" + Escape(e.Location));
            if (e.Notes.Length > 0 && method != "REPLY") W("DESCRIPTION:" + Escape(e.Notes));
            if (e.Category.Length > 0) W("CATEGORIES:" + Escape(e.Category));
            if (e.Private) W("CLASS:PRIVATE");
            W("TRANSP:" + (e.Busy == BusyStatus.Free ? "TRANSPARENT" : "OPAQUE"));
            W("X-MICROSOFT-CDO-BUSYSTATUS:" + e.Busy switch { BusyStatus.Free => "FREE", BusyStatus.Tentative => "TENTATIVE", BusyStatus.OutOfOffice => "OOF", _ => "BUSY" });
            if (method == "CANCEL" || e.Cancelled) W("STATUS:CANCELLED"); else if (method == "REQUEST") W("STATUS:CONFIRMED");
            var org = organizer ?? e.Organizer;
            if (org.Length > 0) W("ORGANIZER" + PersonParams(org) + ":mailto:" + AddressOf(org));
            if (method == "REPLY" && replyFrom != null)
                W("ATTENDEE;PARTSTAT=" + (partStat ?? "ACCEPTED") + PersonParams(replyFrom) + ":mailto:" + AddressOf(replyFrom));
            else
                foreach (var a in e.Attendees)
                    W("ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE" + PersonParams(a) + ":mailto:" + AddressOf(a));
            if (e.Repeat is { Frequency: not RepeatFrequency.None } r) W("RRULE:" + WriteRule(r));
            foreach (var ex in e.Exceptions) W("EXDATE:" + Utc(ex));
            if (e.ReminderMinutes >= 0 && method is "PUBLISH" or "REQUEST")
            {
                W("BEGIN:VALARM"); W("ACTION:DISPLAY"); W("DESCRIPTION:" + Escape(e.Title)); W($"TRIGGER:-PT{e.ReminderMinutes}M"); W("END:VALARM");
            }
            W("END:VEVENT");
        }
        W("END:VCALENDAR");
        return sb.ToString();
    }

    public static string WriteRule(RepeatRule r)
    {
        var parts = new List<string> { "FREQ=" + r.Frequency.ToString().ToUpperInvariant() };
        if (r.Interval > 1) parts.Add("INTERVAL=" + r.Interval.ToString(CultureInfo.InvariantCulture));
        if (r.Frequency == RepeatFrequency.Weekly && r.Days.Count > 0) parts.Add("BYDAY=" + string.Join(",", r.Days.OrderBy(d => ((int)d + 6) % 7).Select(DayCode)));
        if (r.Frequency is RepeatFrequency.Monthly or RepeatFrequency.Yearly && r.WeekOfMonth is { } n && r.Days.Count > 0) parts.Add("BYDAY=" + n.ToString(CultureInfo.InvariantCulture) + DayCode(r.Days[0]));
        if (r.Count is { } c) parts.Add("COUNT=" + c.ToString(CultureInfo.InvariantCulture));
        else if (r.Until is { } u) parts.Add("UNTIL=" + Utc(u.Date.AddDays(1).AddSeconds(-1)));
        return string.Join(";", parts);
    }

    static string PersonParams(string person)
    {
        var name = NameOf(person);
        return name.Length > 0 ? ";CN=\"" + name.Replace("\"", "'") + "\"" : "";
    }

    public static string AddressOf(string person)
    {
        var m = Regex.Match(person, @"<([^>]+)>");
        return (m.Success ? m.Groups[1].Value : person).Trim();
    }

    public static string NameOf(string person)
    {
        var i = person.IndexOf('<');
        return i > 0 ? person[..i].Trim().Trim('"') : "";
    }

    static string Utc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    static void Fold(StringBuilder sb, string line)
    {
        // 75 octets per line (UTF-8 safe: never split a multi-byte character)
        var bytes = 0; var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var len = Encoding.UTF8.GetByteCount(line.AsSpan(i, char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1));
            if (bytes + len > (start == 0 ? 75 : 74)) { sb.Append(line, start, i - start).Append("\r\n "); start = i; bytes = 0; }
            bytes += len; if (char.IsHighSurrogate(line[i])) i++;
        }
        sb.Append(line, start, line.Length - start).Append("\r\n");
    }

    static DateTime AsLocalWall(this DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
}

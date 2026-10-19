using System.Text.Json.Serialization;

namespace MailStudio.Services.Calendar;

public enum RepeatFrequency { None, Daily, Weekly, Monthly, Yearly }

/// <summary>Reply of the user to a meeting (Outlook "Accept / Tentative / Decline").</summary>
public enum MeetingResponse { None, Accepted, Tentative, Declined, Organizer }

public enum BusyStatus { Busy, Tentative, Free, OutOfOffice }

/// <summary>Repeat rule (RFC 5545 RRULE subset that Outlook and Google use).</summary>
public sealed class RepeatRule
{
    public RepeatFrequency Frequency { get; set; }
    public int Interval { get; set; } = 1;
    /// <summary>Weekly: the days (empty = the start's day).  Monthly with <see cref="WeekOfMonth"/>: the single day.</summary>
    public List<DayOfWeek> Days { get; set; } = new();
    /// <summary>Monthly/yearly "the 2nd Tuesday": 1..4, or -1 for the last. Null = same day number as the start.</summary>
    public int? WeekOfMonth { get; set; }
    public int? Count { get; set; }
    /// <summary>Last date (inclusive, local) on which an occurrence may start.</summary>
    public DateTime? Until { get; set; }

    [JsonIgnore] public bool IsWorkdays => Frequency == RepeatFrequency.Weekly && Interval == 1 && Days.Count == 5 &&
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }.All(Days.Contains);
}

/// <summary>An appointment or meeting. Times are local wall-clock times of this computer.</summary>
public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>iCalendar UID (the same for every copy of a meeting invitation).</summary>
    public string Uid { get; set; } = Guid.NewGuid().ToString("N") + "@mailstudio";
    public int Sequence { get; set; }
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AllDay { get; set; }
    /// <summary>Minutes before the start; -1 = no reminder.</summary>
    public int ReminderMinutes { get; set; } = 15;
    public RepeatRule? Repeat { get; set; }
    /// <summary>Start times of single occurrences deleted from a series (EXDATE).</summary>
    public List<DateTime> Exceptions { get; set; } = new();
    public string Organizer { get; set; } = "";
    /// <summary>"Name &lt;address&gt;" or plain addresses.</summary>
    public List<string> Attendees { get; set; } = new();
    public MeetingResponse Response { get; set; }
    public BusyStatus Busy { get; set; }
    /// <summary>Colour category name (Kırmızı, Mavi, ...); empty = default.</summary>
    public string Category { get; set; } = "";
    public bool Cancelled { get; set; }
    public bool Private { get; set; }
    /// <summary>Account / folder / uid of the mail this event came from (invitation or "create meeting from mail").</summary>
    public string? SourceMail { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;

    [JsonIgnore] public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    [JsonIgnore] public bool IsMeeting => Attendees.Count > 0 || Organizer.Length > 0;

    public CalendarEvent Clone()
    {
        var c = (CalendarEvent)MemberwiseClone();
        c.Attendees = new(Attendees); c.Exceptions = new(Exceptions);
        if (Repeat != null) c.Repeat = new RepeatRule { Frequency = Repeat.Frequency, Interval = Repeat.Interval, Days = new(Repeat.Days), WeekOfMonth = Repeat.WeekOfMonth, Count = Repeat.Count, Until = Repeat.Until };
        return c;
    }
}

/// <summary>One occurrence of an event (a series expands into many).</summary>
public readonly record struct Occurrence(CalendarEvent Event, DateTime Start, DateTime End)
{
    public string Key => Event.Id + "|" + Start.Ticks;
    public bool Overlaps(DateTime from, DateTime to) => Start < to && (End > from || (End == Start && Start >= from));
}

using System.Text;
using System.Text.Json;

namespace MailStudio.Services.Calendar;

/// <summary>
/// The local calendar (Outlook "Calendar"): appointments, meetings and their reminders, saved in
/// %AppData%\MailStudio\calendar.json. Nothing leaves the computer; invitations are answered by mail only when the user sends them.
/// </summary>
public sealed class CalendarStore
{
    sealed class Data
    {
        public List<CalendarEvent> Events { get; set; } = new();
        /// <summary>Reminders the user dismissed: occurrence keys ("id|ticks").</summary>
        public HashSet<string> Dismissed { get; set; } = new();
        /// <summary>Snoozed reminders: occurrence key → show again at.</summary>
        public Dictionary<string, DateTime> Snoozed { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string _path;
    readonly object _lock = new();
    Data _data = new();
    public event Action? Changed;

    DateTime _stamp;

    public CalendarStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "calendar.json");
        Load();
    }

    void Load()
    {
        try
        {
            if (File.Exists(_path)) { _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new(); _stamp = File.GetLastWriteTimeUtc(_path); }
        }
        catch (JsonException)
        {
            // A damaged file is kept next to it instead of being overwritten with an empty calendar
            try { File.Copy(_path, _path + ".bozuk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch (IOException) { }
            _data = new();
        }
        catch (IOException) { }
    }

    /// <summary>Picks up changes another process wrote before this one writes (no lost agent edits).</summary>
    void Sync()
    {
        try { if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _stamp) lock (_lock) Load(); }
        catch (IOException) { }
    }

    /// <summary>Re-reads the file when another process (the MCP agent server) changed it. Returns true if reloaded.</summary>
    public bool ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(_path) || File.GetLastWriteTimeUtc(_path) == _stamp) return false;
            lock (_lock) Load();
        }
        catch (IOException) { return false; }
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<CalendarEvent> Events { get { lock (_lock) return _data.Events.ToList(); } }

    public CalendarEvent? Find(string id) { lock (_lock) return _data.Events.FirstOrDefault(e => e.Id == id); }
    public CalendarEvent? FindByUid(string uid) { lock (_lock) return _data.Events.FirstOrDefault(e => e.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase)); }

    public void Save(CalendarEvent e)
    { Sync();
        lock (_lock)
        {
            if (e.End < e.Start) e.End = e.Start;
            e.Modified = DateTime.Now;
            var i = _data.Events.FindIndex(x => x.Id == e.Id);
            if (i >= 0) _data.Events[i] = e; else _data.Events.Add(e);
            // A moved event reminds again
            _data.Dismissed.RemoveWhere(k => k.StartsWith(e.Id + "|", StringComparison.Ordinal) && !IsPastKey(k));
        }
        Write();
    }

    static bool IsPastKey(string key) => long.TryParse(key[(key.IndexOf('|') + 1)..], out var t) && new DateTime(t) < DateTime.Now.AddHours(-1);

    public void Delete(CalendarEvent e) { Sync(); lock (_lock) _data.Events.RemoveAll(x => x.Id == e.Id); Write(); }

    /// <summary>Deletes one occurrence of a series (Outlook "delete this occurrence").</summary>
    public void DeleteOccurrence(CalendarEvent e, DateTime start)
    { Sync();
        lock (_lock)
        {
            var stored = _data.Events.FirstOrDefault(x => x.Id == e.Id);
            if (stored == null) return;
            if (stored.Repeat == null) { _data.Events.Remove(stored); }
            else stored.Exceptions.Add(start);
        }
        Write();
    }

    /// <summary>All occurrences overlapping [from, to), sorted (all-day first, then by start).</summary>
    public List<Occurrence> Occurrences(DateTime from, DateTime to, bool includeCancelled = true)
    {
        List<CalendarEvent> events;
        lock (_lock) events = _data.Events.ToList();
        return events.Where(e => includeCancelled || !e.Cancelled)
            .SelectMany(e => Recurrence.Expand(e, from, to))
            .OrderBy(o => o.Start.Date).ThenByDescending(o => o.Event.AllDay).ThenBy(o => o.Start).ThenBy(o => o.Event.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Events in conflict with [start, end) (busy or tentative, not the event itself).</summary>
    public List<Occurrence> Conflicts(DateTime start, DateTime end, string? exceptId = null) =>
        Occurrences(start, end, includeCancelled: false)
            .Where(o => o.Event.Id != exceptId && !o.Event.AllDay && o.Event.Busy != BusyStatus.Free && o.Event.Response != MeetingResponse.Declined && o.Start < end && o.End > start)
            .ToList();

    // ------------------------------------------------------------------ reminders

    /// <summary>Reminders that should be shown now (kept until dismissed or until the item has ended), so importing
    /// an old calendar or opening MailStudio in the evening does not flood the window with past items.</summary>
    public List<Occurrence> DueReminders(DateTime now)
    {
        var list = new List<Occurrence>();
        HashSet<string> dismissed; Dictionary<string, DateTime> snoozed;
        lock (_lock) { dismissed = new(_data.Dismissed); snoozed = new(_data.Snoozed); }
        foreach (var o in Occurrences(now.AddHours(-12), now.AddDays(15), includeCancelled: false))
        {
            if (o.Event.ReminderMinutes < 0 || o.Event.Response == MeetingResponse.Declined || o.End <= now) continue; // nothing to remind after it ended
            if (dismissed.Contains(o.Key)) continue;
            var at = snoozed.TryGetValue(o.Key, out var s) ? s : o.Start.AddMinutes(-o.Event.ReminderMinutes);
            if (at <= now) list.Add(o);
        }
        return list.OrderBy(o => o.Start).ToList();
    }

    public void Dismiss(IEnumerable<Occurrence> items)
    { Sync();
        lock (_lock) foreach (var o in items) { _data.Dismissed.Add(o.Key); _data.Snoozed.Remove(o.Key); }
        Prune(); Write();
    }

    public void Snooze(IEnumerable<Occurrence> items, DateTime until)
    { Sync();
        lock (_lock) foreach (var o in items) _data.Snoozed[o.Key] = until;
        Write();
    }

    void Prune()
    {
        lock (_lock)
        {
            _data.Dismissed.RemoveWhere(k => long.TryParse(k[(k.IndexOf('|') + 1)..], out var t) && new DateTime(t) < DateTime.Now.AddDays(-30));
            foreach (var k in _data.Snoozed.Where(kv => kv.Value < DateTime.Now.AddDays(-30)).Select(kv => kv.Key).ToList()) _data.Snoozed.Remove(k);
        }
    }

    // ------------------------------------------------------------------ iCalendar

    /// <summary>Imports a .ics file (Outlook / Google export, or an invitation). Same UID = updated, not duplicated.</summary>
    public (int added, int updated) ImportIcs(string path) => Import(Ics.Parse(ReadText(path)).Events);

    public (int added, int updated) Import(IEnumerable<CalendarEvent> events)
    { Sync();
        int added = 0, updated = 0;
        lock (_lock)
        {
            foreach (var e in events)
            {
                var existing = _data.Events.FirstOrDefault(x => x.Uid.Equals(e.Uid, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { _data.Events.Add(e); added++; continue; }
                if (e.Sequence < existing.Sequence) continue;
                e.Id = existing.Id; e.Response = e.Response == MeetingResponse.None ? existing.Response : e.Response;
                if (e.Category.Length == 0) e.Category = existing.Category;
                _data.Events[_data.Events.IndexOf(existing)] = e; updated++;
            }
        }
        if (added + updated > 0) Write();
        return (added, updated);
    }

    /// <summary>Applies an invitation, an update or a cancellation from a mail. Returns the stored event.</summary>
    public CalendarEvent? ApplyInvitation(IcsCalendar cal, MeetingResponse response, string? sourceMail)
    {
        CalendarEvent? result = null;
        foreach (var e in cal.Events)
        {
            var existing = FindByUid(e.Uid);
            if (cal.IsCancel)
            {
                if (existing != null) { existing.Cancelled = true; Save(existing); result = existing; }
                continue;
            }
            if (existing != null && e.Sequence < existing.Sequence) { result = existing; continue; }
            e.Response = response; e.SourceMail = sourceMail;
            if (response == MeetingResponse.Tentative) e.Busy = BusyStatus.Tentative;
            if (existing != null) { e.Id = existing.Id; e.Category = existing.Category.Length > 0 ? existing.Category : e.Category; if (e.ReminderMinutes < 0) e.ReminderMinutes = existing.ReminderMinutes; }
            else if (e.ReminderMinutes < 0) e.ReminderMinutes = 15;
            Save(e); result = e;
        }
        return result;
    }

    public void ExportIcs(string path, IEnumerable<CalendarEvent>? events = null) =>
        File.WriteAllText(path, Ics.WriteCalendar(events ?? Events), new UTF8Encoding(false));

    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1254).GetString(bytes); }
    }

    void Write()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_data, Json);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        MailStudio.Services.SafeFile.Move(tmp, _path, true);
        _stamp = File.GetLastWriteTimeUtc(_path);
        Changed?.Invoke();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using MailStudio.Models;

namespace MailStudio.Services;

public sealed class SnoozeStore
{
    readonly string _path;
    readonly object _lock = new();
    List<SnoozeItem> _items = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public event Action? Changed;

    public SnoozeStore(string? customPath = null)
    {
        _path = customPath ?? Path.Combine(AppPaths.Root, "snooze.json");
        Load();
    }

    public IReadOnlyList<SnoozeItem> Items
    {
        get
        {
            lock (_lock) return _items.ToList();
        }
    }

    public bool IsSnoozed(string accountId, string folder, uint uid)
    {
        lock (_lock)
        {
            return _items.Any(i =>
                string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                i.Uid == uid);
        }
    }

    public SnoozeItem? Get(string accountId, string folder, uint uid)
    {
        lock (_lock)
        {
            return _items.FirstOrDefault(i =>
                string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                i.Uid == uid);
        }
    }

    public void Snooze(string accountId, string folder, uint uid, DateTimeOffset until, string subject = "", string from = "", bool wasRead = true, bool restoreAsUnread = true)
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(i =>
                string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                i.Uid == uid);

            if (existing != null)
            {
                existing.SnoozedAt = DateTimeOffset.Now;
                existing.SnoozeUntil = until;
                existing.Subject = !string.IsNullOrWhiteSpace(subject) ? subject : existing.Subject;
                existing.From = !string.IsNullOrWhiteSpace(from) ? from : existing.From;
                existing.RestoreAsUnread = restoreAsUnread;
                existing.OriginalReadState = wasRead;
            }
            else
            {
                _items.Add(new SnoozeItem
                {
                    AccountId = accountId,
                    Folder = folder,
                    Uid = uid,
                    Subject = subject,
                    From = from,
                    SnoozedAt = DateTimeOffset.Now,
                    SnoozeUntil = until,
                    RestoreAsUnread = restoreAsUnread,
                    OriginalReadState = wasRead
                });
            }

            Save();
        }
        Changed?.Invoke();
    }

    public bool Unsnooze(string accountId, string folder, uint uid)
    {
        bool removed = false;
        lock (_lock)
        {
            var count = _items.RemoveAll(i =>
                string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                i.Uid == uid);
            removed = count > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public bool Remove(string id)
    {
        bool removed = false;
        lock (_lock)
        {
            var count = _items.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            removed = count > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Checks for due snoozed messages, removes them from active snoozed list, and returns them to be woken up.
    /// </summary>
    public List<SnoozeItem> CheckDue(DateTimeOffset? now = null)
    {
        var cutoff = now ?? DateTimeOffset.Now;
        var due = new List<SnoozeItem>();

        lock (_lock)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].SnoozeUntil <= cutoff)
                {
                    due.Add(_items[i]);
                    _items.RemoveAt(i);
                }
            }

            if (due.Count > 0)
            {
                Save();
            }
        }

        if (due.Count > 0)
        {
            Changed?.Invoke();
        }

        return due;
    }

    // =========================================================================
    // Preset Calculations
    // =========================================================================

    public static DateTimeOffset GetThisEvening(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var evening = new DateTimeOffset(current.Year, current.Month, current.Day, 18, 0, 0, current.Offset);
        if (current >= evening.AddMinutes(-30))
        {
            evening = evening.AddDays(1);
        }
        return evening;
    }

    public static DateTimeOffset GetTomorrowMorning(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var tomorrow = current.Date.AddDays(1);
        return new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 9, 0, 0, current.Offset);
    }

    public static DateTimeOffset GetNextWeek(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var d = current.Date.AddDays(1);
        while (d.DayOfWeek != DayOfWeek.Monday)
        {
            d = d.AddDays(1);
        }
        return new DateTimeOffset(d.Year, d.Month, d.Day, 9, 0, 0, current.Offset);
    }

    public static DateTimeOffset GetThisWeekend(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var d = current.Date.AddDays(1);
        while (d.DayOfWeek != DayOfWeek.Saturday)
        {
            d = d.AddDays(1);
        }
        return new DateTimeOffset(d.Year, d.Month, d.Day, 9, 0, 0, current.Offset);
    }

    // =========================================================================
    // Persistence
    // =========================================================================

    void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var items = JsonSerializer.Deserialize<List<SnoozeItem>>(json, JsonOptions);
                    if (items != null)
                    {
                        _items = items;
                    }
                }
            }
            catch
            {
                _items = new();
            }
        }
    }

    void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_items, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch { }
    }
}

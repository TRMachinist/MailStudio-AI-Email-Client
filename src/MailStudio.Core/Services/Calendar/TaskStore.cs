using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailStudio.Services.Calendar;

public enum TaskPriority { Low, Normal, High }

/// <summary>A to-do item (Outlook "Tasks"). Flagged mails are shown next to these without being copied.</summary>
public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? ReminderAt { get; set; }
    public bool ReminderDismissed { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Category { get; set; } = "";
    /// <summary>"account|folder|uid" of the mail the task was made from.</summary>
    public string? SourceMail { get; set; }
    public string SourceSubject { get; set; } = "";
    public string SourceFrom { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;

    [JsonIgnore] public bool IsOverdue => !Completed && DueDate is { } d && d.Date < DateTime.Today;
    [JsonIgnore] public bool IsDueToday => !Completed && DueDate is { } d && d.Date == DateTime.Today;

    public TaskItem Clone() => (TaskItem)MemberwiseClone();
}

/// <summary>Local task list saved in %AppData%\MailStudio\tasks.json.</summary>
public sealed class TaskStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string _path;
    readonly object _lock = new();
    List<TaskItem> _tasks = new();
    public event Action? Changed;

    DateTime _stamp;

    public TaskStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "tasks.json");
        Load();
    }

    void Load()
    {
        try
        {
            if (File.Exists(_path)) { _tasks = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(_path)) ?? new(); _stamp = File.GetLastWriteTimeUtc(_path); }
        }
        catch (JsonException)
        {
            try { File.Copy(_path, _path + ".bozuk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch (IOException) { }
            _tasks = new();
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

    public IReadOnlyList<TaskItem> Tasks { get { lock (_lock) return _tasks.ToList(); } }

    public TaskItem? Find(string id) { lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id); }
    public TaskItem? FindBySource(string source) { lock (_lock) return _tasks.FirstOrDefault(t => t.SourceMail == source); }

    public void Save(TaskItem t)
    { Sync();
        lock (_lock)
        {
            t.Modified = DateTime.Now;
            if (t.Completed && t.CompletedAt == null) t.CompletedAt = DateTime.Now;
            if (!t.Completed) t.CompletedAt = null;
            var i = _tasks.FindIndex(x => x.Id == t.Id);
            if (i >= 0) _tasks[i] = t; else _tasks.Add(t);
        }
        Write();
    }

    public void Delete(TaskItem t) { Sync(); lock (_lock) _tasks.RemoveAll(x => x.Id == t.Id); Write(); }

    public void SetCompleted(TaskItem t, bool done) { t.Completed = done; Save(t); }

    /// <summary>Adds several tasks at once (e.g. "AI: extract tasks"); titles already open in the list are skipped.</summary>
    public int AddRange(IEnumerable<TaskItem> items)
    { Sync();
        var n = 0;
        lock (_lock)
            foreach (var t in items)
            {
                if (string.IsNullOrWhiteSpace(t.Title)) continue;
                if (_tasks.Any(x => !x.Completed && x.Title.Trim().Equals(t.Title.Trim(), StringComparison.CurrentCultureIgnoreCase) && x.SourceMail == t.SourceMail)) continue;
                _tasks.Add(t); n++;
            }
        if (n > 0) Write();
        return n;
    }

    public List<TaskItem> DueReminders(DateTime now)
    {
        lock (_lock) return _tasks.Where(t => !t.Completed && !t.ReminderDismissed && t.ReminderAt is { } r && r <= now && r > now.AddDays(-30)).OrderBy(t => t.ReminderAt).ToList();
    }

    public void DismissReminder(TaskItem t) { t.ReminderDismissed = true; Save(t); }
    public void SnoozeReminder(TaskItem t, DateTime until) { t.ReminderAt = until; t.ReminderDismissed = false; Save(t); }

    /// <summary>Plain-text task lines from an AI answer ("- [ ] Send the quote by Friday", "1. ...").</summary>
    public static List<string> ParseTaskLines(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(?:[-*•–]|\d+[.)])\s*(?:\[[ xX]?\]\s*)?(.+)$");
            if (!m.Success) continue;
            var title = m.Groups[1].Value.Trim().Trim('*').Trim();
            if (title.Length is > 2 and < 300 && !title.EndsWith(':')) result.Add(title);
        }
        return result;
    }

    void Write()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_tasks, Json);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, _path, true);
        _stamp = File.GetLastWriteTimeUtc(_path);
        Changed?.Invoke();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// Persistent storage for Quick Steps macros (quicksteps.json).
/// </summary>
public sealed class QuickStepStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    readonly string _path;
    readonly object _lock = new();
    List<QuickStepItem> _steps = new();

    public QuickStepStore(string? basePath = null)
    {
        _path = Path.Combine(basePath ?? AppPaths.Root, "quicksteps.json");
        Load();
    }

    public IReadOnlyList<QuickStepItem> Steps
    {
        get { lock (_lock) return _steps.OrderBy(s => s.Order).ToList(); }
    }

    public QuickStepItem? FindById(string id)
    {
        lock (_lock) return _steps.FirstOrDefault(s => s.Id == id);
    }

    public QuickStepItem? FindByShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        lock (_lock)
        {
            return _steps.FirstOrDefault(s => string.Equals(s.Shortcut, shortcut, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Save(QuickStepItem step)
    {
        lock (_lock)
        {
            var idx = _steps.FindIndex(s => s.Id == step.Id);
            if (idx >= 0)
            {
                _steps[idx] = step;
            }
            else
            {
                if (step.Order == 0) step.Order = (_steps.Count > 0 ? _steps.Max(s => s.Order) + 1 : 1);
                _steps.Add(step);
            }
            Persist();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _steps.RemoveAll(s => s.Id == id) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    public void ResetToDefaults()
    {
        lock (_lock)
        {
            _steps = CreateDefaultSteps();
            Persist();
        }
    }

    void Load()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                try
                {
                    var text = JsonFile.ReadOrBackup(_path);
                    var list = JsonSerializer.Deserialize<List<QuickStepItem>>(text, JsonOptions);
                    if (list != null && list.Count > 0)
                    {
                        _steps = list;
                        return;
                    }
                }
                catch
                {
                    // Fall back to defaults on corrupt file
                }
            }

            _steps = CreateDefaultSteps();
            Persist();
        }
    }

    void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            JsonFile.WriteAtomic(_path, JsonSerializer.Serialize(_steps, JsonOptions));
        }
        catch { }
    }

    public static List<QuickStepItem> CreateDefaultSteps()
    {
        var en = L.IsEnglish;
        return new List<QuickStepItem>
        {
            new()
            {
                Id = "qs-read-archive",
                Name = en ? "Mark Read + Archive" : "Okundu + Arşive taşı",
                Description = en ? "Marks as read and moves to archive" : "Okundu olarak işaretler ve arşive taşır",
                Icon = "\uE8B7",
                Shortcut = "Ctrl+Shift+1",
                Order = 1,
                IsBuiltIn = true,
                Actions = new List<QuickStepAction>
                {
                    new() { Type = QuickStepActionType.MarkRead },
                    new() { Type = QuickStepActionType.MoveToArchive }
                }
            },
            new()
            {
                Id = "qs-reply-flag",
                Name = en ? "Reply with Template + Flag" : "Şablonla yanıtla + Bayrakla",
                Description = en ? "Opens reply with template and flags" : "Teklif şablonuyla yanıt açar ve bayrak ekler",
                Icon = "\uE715",
                Shortcut = "Ctrl+Shift+2",
                Order = 2,
                IsBuiltIn = true,
                Actions = new List<QuickStepAction>
                {
                    new() { Type = QuickStepActionType.ReplyWithTemplate, Parameter = en ? "quote-attached-en" : "teklif-ektedir-tr" },
                    new() { Type = QuickStepActionType.Flag }
                }
            },
            new()
            {
                Id = "qs-task-move",
                Name = en ? "Create Task + Move" : "Görev yap + Taşı",
                Description = en ? "Creates follow-up task and moves to archive" : "Takip görevi oluşturur ve arşive taşır",
                Icon = "\uE823",
                Shortcut = "Ctrl+Shift+3",
                Order = 3,
                IsBuiltIn = true,
                Actions = new List<QuickStepAction>
                {
                    new() { Type = QuickStepActionType.CreateTask, Parameter = en ? "Follow-up" : "Takip" },
                    new() { Type = QuickStepActionType.MoveToArchive }
                }
            }
        };
    }
}

using System.Text.Json.Serialization;

namespace MailStudio.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuickStepActionType
{
    MarkRead,
    MarkUnread,
    Flag,
    Unflag,
    MoveToArchive,
    MoveToFolder,
    Delete,
    ReplyWithTemplate,
    CreateTask,
    SetCategory
}

public sealed class QuickStepAction
{
    public QuickStepActionType Type { get; set; }
    public string? Parameter { get; set; }

    public QuickStepAction Clone() => new()
    {
        Type = Type,
        Parameter = Parameter
    };
}

/// <summary>
/// Quick step multi-action macro for fast one-click mail processing.
/// </summary>
public sealed class QuickStepItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "\uE715"; // Segoe MDL2 icon glyph
    public string Shortcut { get; set; } = ""; // e.g. "Ctrl+Shift+1"
    public List<QuickStepAction> Actions { get; set; } = new();
    public int Order { get; set; }
    public bool IsBuiltIn { get; set; }
    [JsonIgnore]
    public string DisplayName => L.T(Name);

    public QuickStepItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Icon = Icon,
        Shortcut = Shortcut,
        Actions = Actions.Select(a => a.Clone()).ToList(),
        Order = Order,
        IsBuiltIn = IsBuiltIn
    };
}

using System;
using System.Text.Json.Serialization;

namespace MailStudio.Models;

public sealed class SnoozeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public string Folder { get; set; } = "";
    public uint Uid { get; set; }
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public DateTimeOffset SnoozedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset SnoozeUntil { get; set; }
    public bool RestoreAsUnread { get; set; } = true;
    public bool OriginalReadState { get; set; }

    [JsonIgnore]
    public bool IsDue => SnoozeUntil <= DateTimeOffset.Now;
}

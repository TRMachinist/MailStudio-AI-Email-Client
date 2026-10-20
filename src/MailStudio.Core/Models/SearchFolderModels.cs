using System;
using System.Linq;
using System.Text.Json.Serialization;
using MailStudio.Services;

namespace MailStudio.Models;

public sealed class SearchFolderCriteria
{
    public string? From { get; set; }
    public bool? HasAttachments { get; set; }
    public string? AttachmentType { get; set; }
    public DateTimeOffset? DateFrom { get; set; }
    public DateTimeOffset? DateTo { get; set; }
    public string? DateRangePreset { get; set; }
    public bool? IsUnread { get; set; }
    public bool? IsFlagged { get; set; }
    public string? Category { get; set; }
    public string? Query { get; set; }
    public string? AccountId { get; set; }

    public bool Matches(MailHeader header, DateTimeOffset? now = null, MailCache? cache = null)
    {
        if (header.IsSnoozed) return false;
        if (!string.IsNullOrEmpty(AccountId) && header.AccountId != AccountId) return false;
        if (IsUnread == true && header.IsRead) return false;
        if (IsFlagged == true && !header.IsFlagged) return false;
        if (HasAttachments == true && !header.HasAttachments) return false;
        if (HasAttachments == false && header.HasAttachments) return false;

        // compare in local time: the cache keeps dates in UTC (a 01:00 mail in Turkey is still "today")
        var refDate = (now ?? DateTimeOffset.Now).ToLocalTime();
        var mailLocal = header.Date.ToLocalTime().DateTime;
        if (!string.IsNullOrEmpty(DateRangePreset))
        {
            if (DateRangePreset == "today")
            {
                if (mailLocal.Date < refDate.Date) return false;
            }
            else if (DateRangePreset == "this_week")
            {
                var startOfWeek = MailStudio.Services.Calendar.Recurrence.WeekStart(refDate.Date);
                if (mailLocal < startOfWeek) return false;
            }
            else if (DateRangePreset == "this_month")
            {
                var startOfMonth = new DateTime(refDate.Year, refDate.Month, 1);
                if (mailLocal < startOfMonth) return false;
            }
            else if (DateRangePreset == "last_30_days")
            {
                if (header.Date < refDate.AddDays(-30)) return false;
            }
        }

        if (DateFrom.HasValue && header.Date < DateFrom.Value) return false;
        if (DateTo.HasValue && header.Date > DateTo.Value) return false;

        if (!string.IsNullOrWhiteSpace(From))
        {
            if (!header.FromName.Contains(From, StringComparison.OrdinalIgnoreCase) &&
                !header.FromAddress.Contains(From, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(Category))
        {
            if (string.IsNullOrEmpty(header.Category) || !header.Category.Contains(Category, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(Query))
        {
            if (!header.Subject.Contains(Query, StringComparison.OrdinalIgnoreCase) &&
                !header.Preview.Contains(Query, StringComparison.OrdinalIgnoreCase) &&
                !header.FromName.Contains(Query, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(AttachmentType))
        {
            if (!header.HasAttachments) return false;
            var ext = AttachmentType.Trim().TrimStart('.').ToLowerInvariant();
            if (cache != null)
            {
                var atts = cache.GetAttachments(header.AccountId, header.Folder, header.Uid);
                // attachment names not known yet: do not claim a match
                if (atts.Count == 0 || !atts.Any(a => a.FileName.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) || a.FileName.Contains("." + ext + ".", StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            else
            {
                if (!header.Subject.Contains(ext, StringComparison.OrdinalIgnoreCase) &&
                    !header.Preview.Contains(ext, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    public SearchFolderCriteria Clone() => new()
    {
        From = From,
        HasAttachments = HasAttachments,
        AttachmentType = AttachmentType,
        DateFrom = DateFrom,
        DateTo = DateTo,
        DateRangePreset = DateRangePreset,
        IsUnread = IsUnread,
        IsFlagged = IsFlagged,
        Category = Category,
        Query = Query,
        AccountId = AccountId
    };
}

public sealed class SearchFolderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "\uE721";
    public bool IsBuiltIn { get; set; }
    public SearchFolderCriteria Criteria { get; set; } = new();

    public SearchFolderItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Icon = Icon,
        IsBuiltIn = IsBuiltIn,
        Criteria = Criteria.Clone()
    };
}

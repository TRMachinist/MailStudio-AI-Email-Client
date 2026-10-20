using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// Persistent storage for search folders / saved searches (searchfolders.json).
/// </summary>
public sealed class SearchFolderStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    readonly string _path;
    readonly object _lock = new();
    List<SearchFolderItem> _folders = new();

    public SearchFolderStore(string? basePath = null)
    {
        _path = basePath ?? Path.Combine(AppPaths.Root, "searchfolders.json");
        Load();
    }

    public IReadOnlyList<SearchFolderItem> Folders
    {
        get
        {
            lock (_lock) return _folders.Select(f => f.Clone()).ToList();
        }
    }

    public SearchFolderItem? Find(string id)
    {
        lock (_lock) return _folders.FirstOrDefault(f => f.Id == id)?.Clone();
    }

    public void AddOrUpdate(SearchFolderItem item)
    {
        lock (_lock)
        {
            var idx = _folders.FindIndex(f => f.Id == item.Id);
            if (idx >= 0)
                _folders[idx] = item.Clone();
            else
                _folders.Add(item.Clone());
            Save();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _folders.RemoveAll(f => f.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public List<MailHeader> ExecuteSearch(SearchFolderItem folder, MailCache cache, DateTimeOffset? now = null)
    {
        var headers = cache.GetAllHeaders();
        var refNow = now ?? DateTimeOffset.Now;
        var results = new List<MailHeader>();
        foreach (var h in headers)
        {
            if (folder.Criteria.Matches(h, refNow, cache))
                results.Add(h);
        }
        return results;
    }

    public int CountMatching(SearchFolderItem folder, MailCache cache, bool unreadOnly = false, DateTimeOffset? now = null)
    {
        var headers = cache.GetAllHeaders();
        var refNow = now ?? DateTimeOffset.Now;
        int count = 0;
        foreach (var h in headers)
        {
            if (unreadOnly && h.IsRead) continue;
            if (folder.Criteria.Matches(h, refNow, cache))
                count++;
        }
        return count;
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _folders = JsonSerializer.Deserialize<List<SearchFolderItem>>(json, JsonOptions) ?? new();
                }
            }
            catch
            {
                _folders = new();
            }

            if (_folders.Count == 0)
            {
                _folders = CreateDefaultFolders();
                Save();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_folders, JsonOptions));
            }
            catch { }
        }
    }

    public static List<SearchFolderItem> CreateDefaultFolders()
    {
        var en = L.IsEnglish;
        return new List<SearchFolderItem>
        {
            new()
            {
                Id = "search-unread",
                Name = en ? "Unread" : "Okunmamış",
                Icon = "\uE8C3",
                IsBuiltIn = true,
                Criteria = new SearchFolderCriteria
                {
                    IsUnread = true
                }
            },
            new()
            {
                Id = "search-flagged",
                Name = en ? "Flagged" : "Bayraklı",
                Icon = "\uE7C1",
                IsBuiltIn = true,
                Criteria = new SearchFolderCriteria
                {
                    IsFlagged = true
                }
            },
            new()
            {
                Id = "search-this-week-attachments",
                Name = en ? "This Week's Attachments" : "Bu haftanın ekli mailleri",
                Icon = "\uE723",
                IsBuiltIn = true,
                Criteria = new SearchFolderCriteria
                {
                    HasAttachments = true,
                    DateRangePreset = "this_week"
                }
            }
        };
    }
}

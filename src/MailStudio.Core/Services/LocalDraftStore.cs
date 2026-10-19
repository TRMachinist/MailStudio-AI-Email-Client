using System.Text.Json;
using MailStudio.Models;

namespace MailStudio.Services;

public record LocalDraft(string AccountId, DateTimeOffset Updated, ComposeRequest Request);

public sealed class LocalDraftStore
{
    readonly string _root;
    public LocalDraftStore(string? root = null) => _root = root ?? Path.Combine(AppPaths.Root, "drafts");
    string PathOf(string id) => Path.Combine(_root, Guid.ParseExact(id, "N").ToString("N") + ".json");
    public void Save(string accountId, ComposeRequest request)
    {
        Directory.CreateDirectory(_root);
        var path = PathOf(request.DraftId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new LocalDraft(accountId, DateTimeOffset.Now, request)));
            MailStudio.Services.SafeFile.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    /// <summary>Removes the local copy (no error when it was never saved, e.g. the first mail on a new computer).</summary>
    public void Delete(string id)
    {
        var path = PathOf(id);
        if (File.Exists(path)) File.Delete(path);
    }
    public IReadOnlyList<LocalDraft> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<LocalDraft>();
        return Directory.GetFiles(_root, "*.json").Select(path =>
        {
            try { return JsonSerializer.Deserialize<LocalDraft>(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is IOException or JsonException) { return null; }
        }).OfType<LocalDraft>().OrderByDescending(d => d.Updated).ToList();
    }
}

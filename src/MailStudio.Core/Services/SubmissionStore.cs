using System.Text.Json;
using MimeKit;

namespace MailStudio.Services;

public record SubmissionRecord(string Id, string AccountId, string MessageId, string State, string? Detail, DateTimeOffset Updated);
public record SendResult(string SubmissionId, string? Warning, string? MessageId = null);
public sealed class SubmissionUncertainException(string message, Exception inner) : Exception(message, inner);

/// <summary>Durable audit/recovery record. No automatic replay: SMTP acceptance may be unknown after a disconnect.</summary>
public sealed class SubmissionStore
{
    readonly string _root;
    public SubmissionStore(string? root = null) => _root = root ?? Path.Combine(AppPaths.Root, "submissions");
    public async Task<string> PrepareAsync(string account, MimeMessage message, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        await AttachmentFiles.WriteAsync(Path.Combine(_root, id + ".eml"), stream => message.WriteToAsync(stream, ct), ct);
        Set(id, account, message.MessageId!, "Prepared");
        return id;
    }
    public void Set(string id, string account, string messageId, string state, string? detail = null)
    {
        var path = Path.Combine(_root, Guid.ParseExact(id, "N").ToString("N") + ".json");
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new SubmissionRecord(id, account, messageId, state, detail, DateTimeOffset.Now)));
            MailStudio.Services.SafeFile.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

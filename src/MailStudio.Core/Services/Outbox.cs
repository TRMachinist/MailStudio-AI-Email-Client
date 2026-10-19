using System.Text.Json;
using System.Text.Json.Serialization;
using MailStudio.Models;

namespace MailStudio.Models
{
    public class OutboxItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string AccountId { get; set; } = "";
        public ComposeRequest Request { get; set; } = new();
        public DateTimeOffset ScheduledTime { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset CreatedTime { get; set; } = DateTimeOffset.Now;
        /// <summary>Scheduled | Sending | Sent | Failed | Cancelled</summary>
        public string Status { get; set; } = "Scheduled";
        public string? Error { get; set; }
        public bool IsUndoSend { get; set; }

        public OutboxItem Clone() => new()
        {
            Id = Id,
            AccountId = AccountId,
            Request = Request,
            ScheduledTime = ScheduledTime,
            CreatedTime = CreatedTime,
            Status = Status,
            Error = Error,
            IsUndoSend = IsUndoSend
        };
    }
}

namespace MailStudio.Services
{
    public class OutboxStore
    {
        readonly string _path;
        static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public List<OutboxItem> Items { get; private set; } = new();

        public OutboxStore(string? path = null)
        {
            _path = path ?? Path.Combine(AppPaths.Root, "outbox.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var text = File.ReadAllText(_path);
                    Items = JsonSerializer.Deserialize<List<OutboxItem>>(text, JsonOpts) ?? new();
                    return;
                }
            }
            catch { }
            Items = new();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // Do not persist volatile undo send items across restarts
                var persistable = Items.Where(i => !i.IsUndoSend).ToList();
                var json = JsonSerializer.Serialize(persistable, JsonOpts);
                File.WriteAllText(_path, json);
            }
            catch { }
        }

        public void Add(OutboxItem item)
        {
            Items.Add(item);
            Save();
        }

        public void Remove(string id)
        {
            Items.RemoveAll(i => i.Id == id);
            Save();
        }

        public OutboxItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

        public List<OutboxItem> GetPendingDue(DateTimeOffset? now = null)
        {
            var current = now ?? DateTimeOffset.Now;
            return Items.Where(i => i.Status == "Scheduled" && current >= i.ScheduledTime).ToList();
        }

        public async Task<int> ProcessDueSendsAsync(Func<string, AccountConfig?> getAccount, MailService mail,
            DateTimeOffset? now = null, Action<OutboxItem, SendResult>? onSent = null, CancellationToken ct = default)
        {
            var due = GetPendingDue(now);
            if (due.Count == 0) return 0;

            int sentCount = 0;
            foreach (var item in due)
            {
                var account = getAccount(item.AccountId);
                if (account == null)
                {
                    item.Status = "Failed";
                    item.Error = L.T("Hesap bulunamadı.");
                    continue;
                }

                item.Status = "Sending";
                try
                {
                    var res = await mail.SendAsync(account, item.Request, ct);
                    item.Status = "Sent";
                    item.Error = null;
                    sentCount++;
                    onSent?.Invoke(item, res);
                    Items.Remove(item);
                }
                catch (Exception ex)
                {
                    item.Status = "Failed";
                    item.Error = ex.Message;
                }
            }

            Save();
            return sentCount;
        }
    }
}

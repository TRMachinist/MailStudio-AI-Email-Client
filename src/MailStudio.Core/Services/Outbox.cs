using System.Text;
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
        /// <summary>
        /// Scheduled: sent automatically when due (only while the app runs).
        /// Held: its time passed while the app was closed - waits for the user ("send now" / edit / delete).
        /// Sending: handed to SMTP. After a crash it becomes Uncertain and is never sent again automatically.
        /// Failed: SMTP refused it before submission; the user decides.
        /// </summary>
        public string Status { get; set; } = OutboxStatus.Scheduled;
        public string? Error { get; set; }
        /// <summary>Short "undo send" delay (seconds) rather than a send-later time.</summary>
        public bool IsUndoSend { get; set; }

        [JsonIgnore] public bool IsWaiting => Status is OutboxStatus.Scheduled;
        [JsonIgnore] public bool CanUserAct => Status is OutboxStatus.Scheduled or OutboxStatus.Held or OutboxStatus.Failed or OutboxStatus.Uncertain;
    }

    public static class OutboxStatus
    {
        public const string Scheduled = "Scheduled";
        public const string Held = "Held";
        public const string Sending = "Sending";
        public const string Failed = "Failed";
        public const string Uncertain = "Uncertain";
    }
}

namespace MailStudio.Services
{
    /// <summary>
    /// Undo-send and send-later queue (outbox.json). Every state change is written to disk (atomically) BEFORE
    /// SMTP is contacted, so a crash can never cause a silent second send.
    /// </summary>
    public class OutboxStore
    {
        readonly string _path;
        readonly object _lock = new();
        readonly SemaphoreSlim _sending = new(1, 1);
        static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        List<OutboxItem> _items = new();
        public List<OutboxItem> Items { get { lock (_lock) return _items.ToList(); } }
        public event Action? Changed;

        public OutboxStore(string? path = null)
        {
            _path = path ?? Path.Combine(AppPaths.Root, "outbox.json");
            Load();
        }

        /// <summary>
        /// Reads the queue. Items whose time passed while the app was closed are put on hold (the user is asked),
        /// an item that was being sent when the app stopped becomes Uncertain (it may already have gone out).
        /// </summary>
        public void Load(DateTimeOffset? now = null)
        {
            var t = now ?? DateTimeOffset.Now;
            List<OutboxItem> items;
            try
            {
                items = File.Exists(_path) ? JsonSerializer.Deserialize<List<OutboxItem>>(File.ReadAllText(_path), JsonOpts) ?? new() : new();
            }
            catch (JsonException)
            {
                try { File.Copy(_path, _path + ".bozuk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch (IOException) { }
                items = new();
            }
            catch (IOException) { items = new(); }

            var changed = false;
            foreach (var i in items)
            {
                if (i.Status == OutboxStatus.Sending) { i.Status = OutboxStatus.Uncertain; i.Error = L.T("Gönderilirken program kapandı; ileti gitmiş olabilir. Gönderilmiş Öğeler'i kontrol edin."); changed = true; }
                else if (i.Status == OutboxStatus.Scheduled && (i.IsUndoSend || i.ScheduledTime <= t)) { i.Status = OutboxStatus.Held; i.IsUndoSend = false; changed = true; }
            }
            lock (_lock) _items = items;
            if (changed) Save();
        }

        public void Save()
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_items, JsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            SafeFile.Move(tmp, _path, true);
            Changed?.Invoke();
        }

        public void Add(OutboxItem item) { lock (_lock) _items.Add(item); Save(); }

        /// <summary>Removes an item the user acts on (undo / edit / delete). Not possible while it is being sent.</summary>
        public bool Remove(string id)
        {
            lock (_lock)
            {
                var i = _items.FirstOrDefault(x => x.Id == id);
                if (i == null || i.Status == OutboxStatus.Sending) return false;
                _items.Remove(i);
            }
            Save();
            return true;
        }

        public OutboxItem? Find(string id) { lock (_lock) return _items.FirstOrDefault(i => i.Id == id); }

        public List<OutboxItem> Held { get { lock (_lock) return _items.Where(i => i.Status == OutboxStatus.Held).ToList(); } }

        /// <summary>The user chose "send now": the item is sent on the next queue run.</summary>
        public bool Release(string id)
        {
            lock (_lock)
            {
                var i = _items.FirstOrDefault(x => x.Id == id);
                if (i == null || !i.CanUserAct) return false;
                i.Status = OutboxStatus.Scheduled; i.Error = null; i.ScheduledTime = DateTimeOffset.Now; i.IsUndoSend = false;
            }
            Save();
            return true;
        }

        public List<OutboxItem> GetPendingDue(DateTimeOffset? now = null)
        {
            var current = now ?? DateTimeOffset.Now;
            lock (_lock) return _items.Where(i => i.Status == OutboxStatus.Scheduled && current >= i.ScheduledTime).ToList();
        }

        /// <summary>
        /// Sends what is due, one at a time; a second caller while this runs returns 0 (no double send).
        /// <paramref name="send"/> does the real send (and the after-send work); an exception marks the item Failed,
        /// a <see cref="SubmissionUncertainException"/> marks it Uncertain.
        /// </summary>
        public async Task<int> ProcessDueSendsAsync(Func<string, AccountConfig?> getAccount, Func<AccountConfig, OutboxItem, Task> send,
            DateTimeOffset? now = null, Action<OutboxItem, Exception>? onFailed = null, CancellationToken ct = default)
        {
            if (!await _sending.WaitAsync(0, ct)) return 0;
            try
            {
                var sent = 0;
                foreach (var item in GetPendingDue(now))
                {
                    ct.ThrowIfCancellationRequested();
                    var account = getAccount(item.AccountId);
                    lock (_lock)
                    {
                        if (item.Status != OutboxStatus.Scheduled || !_items.Contains(item)) continue;   // undone / edited meanwhile
                        if (account == null) { item.Status = OutboxStatus.Failed; item.Error = L.T("Hesap bulunamadı."); }
                        else item.Status = OutboxStatus.Sending;
                    }
                    Save();   // on disk BEFORE SMTP is contacted
                    if (account == null) { onFailed?.Invoke(item, new InvalidOperationException(item.Error)); continue; }
                    try
                    {
                        await send(account, item);
                        lock (_lock) _items.Remove(item);
                        Save();
                        sent++;
                    }
                    catch (Exception ex) when (item.Request.SubmissionState == "Completed")
                    {
                        // the mail went out; only the after-send work failed
                        lock (_lock) _items.Remove(item);
                        Save();
                        sent++;
                    }
                    catch (Exception ex)
                    {
                        lock (_lock)
                        {
                            item.Status = ex is SubmissionUncertainException || item.Request.SubmissionState is "Sending" or "Uncertain" or "Accepted"
                                ? OutboxStatus.Uncertain : OutboxStatus.Failed;
                            item.Error = ex.Message;
                        }
                        Save();
                        onFailed?.Invoke(item, ex);
                    }
                }
                return sent;
            }
            finally { _sending.Release(); }
        }
    }
}

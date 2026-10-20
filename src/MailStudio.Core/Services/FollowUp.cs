using System.Text.Json;
using System.Text.Json.Serialization;
using MailStudio.Models;

namespace MailStudio.Models
{
    public class FollowUpItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string MessageId { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Recipients { get; set; } = "";
        public DateTimeOffset SentDate { get; set; } = DateTimeOffset.Now;
        public int DueDays { get; set; } = 3;
        public DateTimeOffset DueDate { get; set; }
        public bool IsResolved { get; set; }
        public DateTimeOffset? ResolvedDate { get; set; }
        public string? ReplyMessageId { get; set; }
        public bool TaskCreated { get; set; }
        public string? TaskId { get; set; }
        public string? CustomNotes { get; set; }

        public FollowUpItem Clone() => (FollowUpItem)MemberwiseClone();
    }
}

namespace MailStudio.Services
{
    public class FollowUpStore
    {
        readonly string _path;
        static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public List<FollowUpItem> FollowUps { get; private set; } = new();

        public FollowUpStore(string? path = null)
        {
            _path = path ?? Path.Combine(AppPaths.Root, "followups.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var text = JsonFile.ReadOrBackup(_path);
                    FollowUps = JsonSerializer.Deserialize<List<FollowUpItem>>(text, JsonOpts) ?? new();
                    return;
                }
            }
            catch { }
            FollowUps = new();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(FollowUps, JsonOpts);
                JsonFile.WriteAtomic(_path, json);
            }
            catch { }
        }

        public void Add(FollowUpItem item)
        {
            FollowUps.Add(item);
            Save();
        }

        /// <summary>
        /// Checks incoming headers for replies. If a reply is found via InReplyTo, References, or thread match,
        /// the follow-up is automatically resolved and any linked task completed.
        /// </summary>
        public int CheckReplies(IEnumerable<MailHeader> headers, Calendar.TaskStore? taskStore = null)
        {
            int resolvedCount = 0;
            var active = FollowUps.Where(f => !f.IsResolved).ToList();
            if (active.Count == 0) return 0;

            foreach (var h in headers)
            {
                var inReplyTo = (h.InReplyTo ?? "").Trim();
                var refs = h.References ?? new();
                var sender = (h.FromAddress ?? "").ToLowerInvariant();

                foreach (var item in active)
                {
                    if (item.IsResolved) continue;

                    bool match = false;
                    if (!string.IsNullOrEmpty(item.MessageId))
                    {
                        var cleanMid = item.MessageId.Trim('<', '>', ' ');
                        if (!string.IsNullOrEmpty(inReplyTo) && inReplyTo.Contains(cleanMid, StringComparison.OrdinalIgnoreCase))
                            match = true;
                        else if (refs.Any(r => r.Contains(cleanMid, StringComparison.OrdinalIgnoreCase)))
                            match = true;
                    }

                    if (!match && !string.IsNullOrEmpty(item.Subject))
                    {
                        // Fallback thread and sender match
                        if (h.ThreadKey == ThreadKey(item.Subject) && item.Recipients.ToLowerInvariant().Contains(sender))
                            match = true;
                    }

                    if (match)
                    {
                        item.IsResolved = true;
                        item.ResolvedDate = DateTimeOffset.Now;
                        item.ReplyMessageId = h.MessageId;
                        resolvedCount++;

                        if (item.TaskCreated && !string.IsNullOrEmpty(item.TaskId) && taskStore != null)
                        {
                            var t = taskStore.Find(item.TaskId);
                            if (t != null && !t.Completed)
                            {
                                taskStore.SetCompleted(t, true);
                            }
                        }
                    }
                }
            }

            if (resolvedCount > 0) Save();
            return resolvedCount;
        }

        static string ThreadKey(string subject)
        {
            var s = System.Text.RegularExpressions.Regex.Replace(subject ?? "",
                @"^(\s*(re|fw|fwd|ynt|yanıt|ilt|i̇lt|İlt|ilet|aw|wg|sv|tr|cevap)\s*(\[\d+\])?\s*:\s*)+", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return s.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// When a follow-up deadline passes without a reply, creates a reminder task in TaskStore under "Cevap bekleniyor".
        /// </summary>
        public int CheckExpired(Calendar.TaskStore taskStore, DateTimeOffset? now = null)
        {
            var current = now ?? DateTimeOffset.Now;
            int createdCount = 0;
            var expired = FollowUps.Where(f => !f.IsResolved && !f.TaskCreated && current >= f.DueDate).ToList();

            foreach (var item in expired)
            {
                var task = new Calendar.TaskItem
                {
                    Title = L.F("Cevap bekleniyor: {0}", item.Subject),
                    Category = L.T("Cevap bekleniyor"),
                    DueDate = item.DueDate.LocalDateTime.Date,
                    ReminderAt = DateTime.Now,
                    Notes = L.F("Alıcılar: {0}\nGönderim tarihi: {1:dd.MM.yyyy HH:mm}\nSon tarih: {2:dd.MM.yyyy}", item.Recipients, item.SentDate, item.DueDate),
                    SourceMail = $"{item.AccountId}|Sent|{item.MessageId}",
                    SourceSubject = item.Subject,
                    SourceFrom = item.Recipients
                };
                taskStore.Save(task);
                item.TaskCreated = true;
                item.TaskId = task.Id;
                createdCount++;
            }

            if (createdCount > 0)
            {
                Save();
            }
            return createdCount;
        }
    }
}

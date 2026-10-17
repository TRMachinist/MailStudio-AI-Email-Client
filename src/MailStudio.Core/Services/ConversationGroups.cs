using MailStudio.Models;
using MimeKit;

namespace MailStudio.Services;

public static class ConversationGroups
{
    /// <summary>Message identity graph, scoped to an account. Equal subjects alone never join conversations.</summary>
    public static IEnumerable<IGrouping<string, MailHeader>> Group(List<MailHeader> messages)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string id)
        {
            if (!parent.ContainsKey(id)) return parent[id] = id;
            var current = id;
            while (parent[current] != current) current = parent[current];
            var root = current;
            current = id;
            while (parent[current] != current) { var next = parent[current]; parent[current] = root; current = next; }
            return root;
        }
        void Join(string a, string b)
        {
            a = Find(a); b = Find(b);
            if (a != b) parent[string.CompareOrdinal(a, b) < 0 ? b : a] = string.CompareOrdinal(a, b) < 0 ? a : b;
        }
        string Key(MailHeader h, string id) => h.AccountId + "\n" + id;
        foreach (var h in messages)
        {
            var self = Key(h, string.IsNullOrEmpty(h.MessageId) ? $"uid:{h.Folder}/{h.UidValidity}/{h.Uid}" : h.MessageId);
            Find(self);
            foreach (var id in h.References.Append(h.InReplyTo).Where(x => !string.IsNullOrWhiteSpace(x))) Join(self, Key(h, id));
        }
        foreach (var h in messages)
            h.ConversationKey = Find(Key(h, string.IsNullOrEmpty(h.MessageId) ? $"uid:{h.Folder}/{h.UidValidity}/{h.Uid}" : h.MessageId));
        return messages.GroupBy(h => h.ConversationKey);
    }

    public static (string To, string Cc) ReplyRecipients(MailHeader h, MailContent content, IEnumerable<string> ownAddresses, bool all)
    {
        var own = ownAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<MailboxAddress> Parse(string text) => InternetAddressList.TryParse(text, out var list) ? list.Mailboxes : Enumerable.Empty<MailboxAddress>();
        var replyTo = string.IsNullOrWhiteSpace(content.ReplyTo) ? h.ReplyTo : content.ReplyTo;
        var primary = Parse(string.IsNullOrWhiteSpace(replyTo) ? h.FromAddress : replyTo).Where(m => !own.Contains(m.Address)).ToList();
        // Replying to our own sent item targets its recipients.
        if (primary.Count == 0) primary = Parse(h.To).Where(m => !own.Contains(m.Address)).ToList();
        string Format(IEnumerable<MailboxAddress> values) => string.Join("; ", values.Where(m => !own.Contains(m.Address) && used.Add(m.Address)).Select(m => m.ToString()));
        var to = Format(primary);
        var cc = all ? Format(Parse(h.To).Concat(Parse(content.Cc))) : "";
        return (to, cc);
    }
}

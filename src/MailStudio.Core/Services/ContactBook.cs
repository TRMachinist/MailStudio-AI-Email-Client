using MailStudio.Models;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailStudio.Services;

public record ContactInfo(string Name, string Address, int Count, DateTimeOffset Last)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
    public string Initials
    {
        get
        {
            var src = string.IsNullOrWhiteSpace(Name) ? Address : Name;
            var p = src.Split(new[] { ' ', '.', '@', '_' }, StringSplitOptions.RemoveEmptyEntries);
            return p.Length == 0 ? "?" : p.Length == 1 ? p[0][..1].ToUpperInvariant() : (p[0][..1] + p[1][..1]).ToUpperInvariant();
        }
    }
}

/// <summary>
/// Like Outlook's "auto-complete list": builds a contact list from the senders/recipients of the cached mails.
/// Recipients of mails you sent yourself get a higher score.
/// </summary>
public class ContactBook
{
    readonly MailCache _cache;
    List<ContactInfo> _all = new();
    public ContactBook(MailCache cache) => _cache = cache;

    public IReadOnlyList<ContactInfo> All => _all;

    public void Rebuild(IEnumerable<string> ownAddresses)
    {
        var own = new HashSet<string>(ownAddresses, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, (string name, int score, DateTimeOffset last)>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, string? addr, int w, DateTimeOffset date)
        {
            if (string.IsNullOrWhiteSpace(addr) || !addr.Contains('@') || own.Contains(addr)) return;
            if (addr.Contains("noreply", StringComparison.OrdinalIgnoreCase) || addr.Contains("no-reply", StringComparison.OrdinalIgnoreCase)) w = Math.Min(w, 1);
            name = MailService.FixTurkish(name?.Trim('"', ' ', '\''));
            if (map.TryGetValue(addr, out var e))
                map[addr] = (string.IsNullOrWhiteSpace(e.name) ? name ?? "" : e.name, e.score + w, date > e.last ? date : e.last);
            else map[addr] = (name ?? "", w, date);
        }

        foreach (var (fromName, fromAddr, to, cc, date) in _cache.GetAddressRows())
        {
            var mine = own.Contains(fromAddr);
            Add(fromName, fromAddr, 1, date);
            foreach (var list in new[] { to, cc })
                if (!string.IsNullOrWhiteSpace(list) && InternetAddressList.TryParse(list, out var parsed))
                    foreach (var mb in parsed.Mailboxes) Add(mb.Name, mb.Address, mine ? 5 : 1, date);
        }
        _all = map.Select(kv => new ContactInfo(kv.Value.name, kv.Key, kv.Value.score, kv.Value.last))
                  .OrderByDescending(c => c.Count).ThenByDescending(c => c.Last).ToList();
    }

    /// <summary>Matches at the start of first name, last name or address (Turkish case-insensitive).</summary>
    public IEnumerable<ContactInfo> Suggest(string query, int max = 8)
    {
        query = query.Trim();
        if (query.Length == 0) return Enumerable.Empty<ContactInfo>();
        var tr = new System.Globalization.CultureInfo("tr-TR");
        var q = query.ToLower(tr);
        return _all.Where(c =>
                c.Address.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                c.Name.ToLower(tr).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(q)) ||
                c.Name.ToLower(tr).StartsWith(q))
            .Take(max);
    }
}

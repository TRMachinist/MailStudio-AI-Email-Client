using MailStudio.Models;
using Microsoft.Data.Sqlite;

namespace MailStudio.Services;

/// <summary>Local SQLite cache: message headers, bodies and FTS5 full-text search.</summary>
public class MailCache
{
    readonly string _cs;
    readonly string _path;
    public MailCache(string? path = null)
    {
        _path = path ?? AppPaths.CacheDb;
        _cs = new SqliteConnectionStringBuilder { DataSource = _path }.ToString();
    }

    /// <summary>Path of the backup if a corrupt cache was found and rebuilt at the last start; otherwise null.</summary>
    public string? RepairedBackup { get; private set; }

    /// <summary>
    /// The cache is only a local copy of the mail on the server. If the SQLite integrity check fails the file
    /// is not deleted but moved to a side folder and an empty cache is created; messages are downloaded again from the server.
    /// </summary>
    public bool CheckAndRepair()
    {
        if (!File.Exists(_path)) { try { File.Delete(RepairMarker); } catch (IOException) { } return false; }
        string? problem = File.Exists(RepairMarker) ? L.T("Çalışırken bozuk sayfa bulundu") : null;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check(5)";
            using var r = cmd.ExecuteReader();
            var lines = new List<string>();
            while (r.Read()) lines.Add(r.GetString(0));
            if (!(lines.Count == 1 && lines[0] == "ok")) problem = string.Join("; ", lines);
        }
        catch (SqliteException ex) { problem = (problem == null ? "" : problem + "; ") + ex.Message; }
        if (problem == null) return false;
        SqliteConnection.ClearAllPools();
        var dir = Path.Combine(Path.GetDirectoryName(_path)!, "cache-bozuk-yedek");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(dir, $"cache-{stamp}.db");
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(_path + suffix)) File.Move(_path + suffix, target + suffix);
        }
        catch (IOException) { return false; } // another process (e.g. MailStudio itself while the agent server starts) still has it open
        catch (UnauthorizedAccessException) { return false; }
        File.WriteAllText(Path.Combine(dir, $"cache-{stamp}.txt"), L.T("SQLite bütünlük kontrolü: ") + problem);
        try { File.Delete(RepairMarker); } catch (IOException) { }
        RepairedBackup = target;
        return true;
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    /// <param name="repair">false for helper processes (MCP server): they must never move the file the app is using.</param>
    public void Init(bool repair = true)
    {
        if (repair) CheckAndRepair();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS messages(
              account TEXT, folder TEXT, uid INTEGER, message_id TEXT,
              from_name TEXT, from_addr TEXT, to_addr TEXT, cc TEXT, subject TEXT, preview TEXT,
              date INTEGER, is_read INTEGER, is_flagged INTEGER, has_att INTEGER,
              body_html TEXT, body_text TEXT,
              PRIMARY KEY(account, folder, uid));
            CREATE INDEX IF NOT EXISTS ix_msg_date ON messages(account, folder, date DESC);
            CREATE TABLE IF NOT EXISTS folder_state(account TEXT, folder TEXT, uidvalidity INTEGER, PRIMARY KEY(account, folder));
            CREATE TABLE IF NOT EXISTS schema_info(version INTEGER);
            CREATE VIRTUAL TABLE IF NOT EXISTS msg_fts USING fts5(account UNINDEXED, folder UNINDEXED, uid UNINDEXED,
              subject, sender, body, tokenize='unicode61 remove_diacritics 2');
            """);
        // v2: attachment list (file name/size/part) — shown as soon as the mail opens
        try { Exec(c, "ALTER TABLE messages ADD COLUMN att_json TEXT"); } catch { /* zaten var */ }
        EnsureMetadata(c);
    }

    // Additive migration, preserving existing mail bodies and full-text data.
    void EnsureMetadata(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(messages)";
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        reader.Close();
        foreach (var (name, type) in new[] { ("is_draft", "INTEGER DEFAULT 0"), ("reply_to", "TEXT"), ("in_reply_to", "TEXT"), ("refs_json", "TEXT"), ("body_encoding_version", "INTEGER NOT NULL DEFAULT 0") })
            if (!names.Contains(name)) Exec(c, L.F("ALTER TABLE messages ADD COLUMN {0} {1}", name, type));
    }

    public void SaveAttachments(string account, string folder, uint uid, List<AttachmentInfo> atts)
    {
        using var c = Open();
        Exec(c, "UPDATE messages SET att_json=$j WHERE account=$a AND folder=$f AND uid=$u",
            ("$j", System.Text.Json.JsonSerializer.Serialize(atts)), ("$a", account), ("$f", folder), ("$u", (long)uid));
    }

    public List<AttachmentInfo> GetAttachments(string account, string folder, uint uid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT att_json FROM messages WHERE account=$a AND folder=$f AND uid=$u";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        cmd.Parameters.AddWithValue("$u", (long)uid);
        var j = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(j)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<AttachmentInfo>>(j) ?? new(); } catch { return new(); }
    }

    static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public uint? GetUidValidity(string account, string folder)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT uidvalidity FROM folder_state WHERE account=$a AND folder=$f";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        var r = cmd.ExecuteScalar();
        return r is long l ? (uint)l : null;
    }

    /// <summary>If UIDVALIDITY changed the folder cache is invalid and is deleted.</summary>
    public void SetUidValidity(string account, string folder, uint value)
    {
        var old = GetUidValidity(account, folder);
        using var c = Open();
        if (old.HasValue && old.Value != value)
        {
            Exec(c, "DELETE FROM messages WHERE account=$a AND folder=$f", ("$a", account), ("$f", folder));
            Exec(c, "DELETE FROM msg_fts WHERE account=$a AND folder=$f", ("$a", account), ("$f", folder));
        }
        Exec(c, "INSERT OR REPLACE INTO folder_state VALUES($a,$f,$v)", ("$a", account), ("$f", folder), ("$v", (long)value));
    }

    public HashSet<uint> GetKnownUids(string account, string folder)
    {
        var set = new HashSet<uint>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT uid FROM messages WHERE account=$a AND folder=$f";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add((uint)r.GetInt64(0));
        return set;
    }

    public void UpsertHeaders(IEnumerable<MailHeader> headers)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var h in headers)
        {
            Exec(c, """
                INSERT INTO messages(account,folder,uid,message_id,from_name,from_addr,to_addr,subject,preview,date,is_read,is_flagged,has_att)
                VALUES($a,$f,$u,$mid,$fn,$fa,$to,$s,$p,$d,$r,$fl,$att)
                ON CONFLICT(account,folder,uid) DO UPDATE SET is_read=$r, is_flagged=$fl,
                  from_name=$fn, from_addr=$fa, to_addr=$to, subject=$s,
                  preview=CASE WHEN length($p)>0 THEN $p ELSE messages.preview END
                """,
                ("$a", h.AccountId), ("$f", h.Folder), ("$u", (long)h.Uid), ("$mid", h.MessageId),
                ("$fn", h.FromName), ("$fa", h.FromAddress), ("$to", h.To), ("$s", h.Subject), ("$p", h.Preview),
                ("$d", h.Date.ToUnixTimeMilliseconds()), ("$r", h.IsRead ? 1 : 0), ("$fl", h.IsFlagged ? 1 : 0),
                ("$att", h.HasAttachments ? 1 : 0));
            Exec(c, "UPDATE messages SET is_draft=$dr,reply_to=$rt,in_reply_to=$ir,refs_json=$refs WHERE account=$a AND folder=$f AND uid=$u",
                ("$dr", h.IsDraft ? 1 : 0), ("$rt", h.ReplyTo), ("$ir", h.InReplyTo), ("$refs", System.Text.Json.JsonSerializer.Serialize(h.References)),
                ("$a", h.AccountId), ("$f", h.Folder), ("$u", (long)h.Uid));
            Exec(c, "DELETE FROM msg_fts WHERE account=$a AND folder=$f AND uid=$u",
                ("$a", h.AccountId), ("$f", h.Folder), ("$u", (long)h.Uid));
            Exec(c, "INSERT INTO msg_fts(account,folder,uid,subject,sender,body) SELECT $a,$f,$u,$s,$snd,COALESCE(body_text,preview,$b) FROM messages WHERE account=$a AND folder=$f AND uid=$u",
                ("$a", h.AccountId), ("$f", h.Folder), ("$u", (long)h.Uid), ("$s", h.Subject),
                ("$snd", h.FromName + " " + h.FromAddress), ("$b", h.Preview));
        }
        tx.Commit();
    }

    public void RemoveMissing(string account, string folder, ICollection<uint> serverUids)
    {
        var known = GetKnownUids(account, folder);
        known.ExceptWith(serverUids);
        if (known.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var uid in known)
        {
            Exec(c, "DELETE FROM messages WHERE account=$a AND folder=$f AND uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid));
            Exec(c, "DELETE FROM msg_fts WHERE account=$a AND folder=$f AND uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid));
        }
        tx.Commit();
    }

    public void SetFlags(string account, string folder, uint uid, bool? read = null, bool? flagged = null)
    {
        using var c = Open();
        if (read.HasValue)
            Exec(c, "UPDATE messages SET is_read=$v WHERE account=$a AND folder=$f AND uid=$u",
                ("$v", read.Value ? 1 : 0), ("$a", account), ("$f", folder), ("$u", (long)uid));
        if (flagged.HasValue)
            Exec(c, "UPDATE messages SET is_flagged=$v WHERE account=$a AND folder=$f AND uid=$u",
                ("$v", flagged.Value ? 1 : 0), ("$a", account), ("$f", folder), ("$u", (long)uid));
    }

    public void Delete(string account, string folder, uint uid)
    {
        using var c = Open();
        Exec(c, "DELETE FROM messages WHERE account=$a AND folder=$f AND uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid));
        Exec(c, "DELETE FROM msg_fts WHERE account=$a AND folder=$f AND uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid));
    }

    public void SaveBody(string account, string folder, uint uid, MailContent content)
    {
        try { SaveBodyCore(account, folder, uid, content); }
        catch (SqliteException ex) when (IsCorrupt(ex)) { OnCorrupt(); }
    }

    void SaveBodyCore(string account, string folder, uint uid, MailContent content)
    {
        using var c = Open();
        Exec(c, "UPDATE messages SET body_html=$h, body_text=$t, cc=$cc, body_encoding_version=2 WHERE account=$a AND folder=$f AND uid=$u",
            ("$h", content.Html), ("$t", content.Text), ("$cc", content.Cc), ("$a", account), ("$f", folder), ("$u", (long)uid));
        Exec(c, "UPDATE msg_fts SET body=$t WHERE account=$a AND folder=$f AND uid=$u",
            ("$t", content.Text), ("$a", account), ("$f", folder), ("$u", (long)uid));
    }

    /// <summary>Set when SQLite reported a damaged file while running; the next start moves it aside and rebuilds.</summary>
    public bool CorruptionDetected { get; private set; }
    public event Action? Corrupted;
    string RepairMarker => _path + ".repair";
    static bool IsCorrupt(SqliteException ex) => ex.SqliteErrorCode is 11 or 26; // SQLITE_CORRUPT, SQLITE_NOTADB
    void OnCorrupt()
    {
        if (CorruptionDetected) return;
        CorruptionDetected = true;
        try { File.WriteAllText(RepairMarker, DateTime.Now.ToString("O")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Corrupted?.Invoke();
    }

    /// <summary>Cached body, or null (= download from the server) when the row is unreadable.</summary>
    public MailContent? GetBody(string account, string folder, uint uid)
    {
        try { return GetBodyCore(account, folder, uid); }
        catch (SqliteException ex) when (IsCorrupt(ex)) { OnCorrupt(); return null; }
    }

    MailContent? GetBodyCore(string account, string folder, uint uid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT body_html, body_text, cc, body_encoding_version FROM messages WHERE account=$a AND folder=$f AND uid=$u AND (body_html IS NOT NULL OR body_text IS NOT NULL)";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        cmd.Parameters.AddWithValue("$u", (long)uid);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var content = new MailContent
        {
            Html = r.IsDBNull(0) ? "" : r.GetString(0),
            Text = r.IsDBNull(1) ? "" : r.GetString(1),
            Cc = r.IsDBNull(2) ? "" : r.GetString(2),
            NeedsEncodingRefresh = r.GetInt32(3) < 2
        };
        r.Close();
        content.Attachments.AddRange(GetAttachments(account, folder, uid));
        return content;
    }

    public List<MailHeader> GetHeaders(string account, string folder, int limit = 50000)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE account=$a AND folder=$f ORDER BY date DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        cmd.Parameters.AddWithValue("$l", limit);
        return ReadHeaders(cmd);
    }

    public List<MailHeader> Search(string? account, string query, int limit = 300)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => "\"" + t.Replace("\"", "") + "\"*");
        var match = string.Join(" ", terms);
        if (string.IsNullOrWhiteSpace(match)) return new();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.* FROM msg_fts f JOIN messages m ON m.account=f.account AND m.folder=f.folder AND m.uid=f.uid
            WHERE msg_fts MATCH $q AND ($a IS NULL OR m.account=$a) ORDER BY m.date DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$a", (object?)account ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", limit);
        return ReadHeaders(cmd);
    }

    /// <summary>Mails whose body has not been downloaded yet (newest first) — for the offline copy.</summary>
    public List<MailHeader> GetMissingBodies(string account, string folder, int limit = 100000)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE account=$a AND folder=$f AND body_html IS NULL AND body_text IS NULL ORDER BY date DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        cmd.Parameters.AddWithValue("$l", limit);
        return ReadHeaders(cmd);
    }

    /// <summary>Mails with attachments and their attachment lists (newest first).</summary>
    public List<(MailHeader h, List<AttachmentInfo> atts)> GetWithAttachments(string account, string folder)
    {
        var list = new List<(MailHeader, List<AttachmentInfo>)>();
        foreach (var h in GetHeaders(account, folder, 100000).Where(x => x.HasAttachments))
        {
            var atts = GetAttachments(account, folder, h.Uid);
            if (atts.Count > 0) list.Add((h, atts));
        }
        return list;
    }

    public (int messages, int withBody) Stats(string account, string folder)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), SUM(CASE WHEN body_html IS NOT NULL OR body_text IS NOT NULL THEN 1 ELSE 0 END) FROM messages WHERE account=$a AND folder=$f";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1));
    }

    /// <summary>All sender/recipient rows for the contact list.</summary>
    public List<(string fromName, string fromAddr, string to, string cc, DateTimeOffset date)> GetAddressRows()
    {
        var list = new List<(string, string, string, string, DateTimeOffset)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT from_name, from_addr, to_addr, cc, date FROM messages";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                      r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                      DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4))));
        return list;
    }

    public MailHeader? GetHeader(string account, string folder, uint uid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE account=$a AND folder=$f AND uid=$u";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        cmd.Parameters.AddWithValue("$u", (long)uid);
        return ReadHeaders(cmd).FirstOrDefault();
    }

    public int UnreadCount(string account, string folder)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE account=$a AND folder=$f AND is_read=0";
        cmd.Parameters.AddWithValue("$a", account);
        cmd.Parameters.AddWithValue("$f", folder);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void RemoveAccount(string account)
    {
        using var c = Open();
        Exec(c, "DELETE FROM messages WHERE account=$a", ("$a", account));
        Exec(c, "DELETE FROM msg_fts WHERE account=$a", ("$a", account));
        Exec(c, "DELETE FROM folder_state WHERE account=$a", ("$a", account));
    }

    List<MailHeader> ReadHeaders(SqliteCommand cmd)
    {
        var list = new List<MailHeader>();
        using var r = cmd.ExecuteReader();
        string S(string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? "" : r.GetString(i); }
        var epochs = new Dictionary<(string, string), uint>();
        uint Epoch(string account, string folder)
        {
            if (!epochs.TryGetValue((account, folder), out var value)) epochs[(account, folder)] = value = GetUidValidity(account, folder) ?? 0;
            return value;
        }
        while (r.Read())
        {
            list.Add(new MailHeader
            {
                AccountId = S("account"), Folder = S("folder"), Uid = (uint)r.GetInt64(r.GetOrdinal("uid")),
                UidValidity = Epoch(S("account"), S("folder")),
                IsDraft = !r.IsDBNull(r.GetOrdinal("is_draft")) && r.GetInt64(r.GetOrdinal("is_draft")) == 1,
                ReplyTo = S("reply_to"), InReplyTo = S("in_reply_to"),
                References = System.Text.Json.JsonSerializer.Deserialize<List<string>>(string.IsNullOrEmpty(S("refs_json")) ? "[]" : S("refs_json")) ?? new(),
                MessageId = S("message_id"), FromName = MailService.FixTurkish(S("from_name")), FromAddress = S("from_addr"),
                To = MailService.FixTurkish(S("to_addr")), Subject = MailService.FixTurkish(S("subject")), Preview = MailService.FixTurkish(MailService.CleanPreview(S("preview"))),
                Date = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("date"))),
                IsRead = r.GetInt64(r.GetOrdinal("is_read")) == 1,
                IsFlagged = r.GetInt64(r.GetOrdinal("is_flagged")) == 1,
                HasAttachments = r.GetInt64(r.GetOrdinal("has_att")) == 1,
            });
        }
        return list;
    }
}

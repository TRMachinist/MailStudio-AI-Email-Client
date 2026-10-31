using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailStudio.Models;
using Microsoft.Data.Sqlite;

namespace MailStudio.Services.Semantic;

/// <summary>Turns texts into vectors whose dot product says how close their meaning is.</summary>
public interface IEmbedder
{
    /// <summary>Stable name stored with every vector; a different id means the index is rebuilt.</summary>
    string Id { get; }
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// Built-in, download-free vectors: word stems and character trigrams hashed into 1024 dimensions. Not a language
/// model, but it matches inflected Turkish ("revizyonu", "revizyonlar"), typos and reordered words, and works on any
/// PC. An Ollama embedding model (e.g. bge-m3) gives real meaning matching when installed.
/// </summary>
public sealed class LocalNgramEmbedder : IEmbedder
{
    public const int Dim = 1024;
    public string Id => "local-ngram-v1";

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        Task.FromResult(texts.Select(Embed).ToArray());

    public static string Normalize(string s)
    {
        s = s.Replace('İ', 'i').Replace('I', 'i').ToLowerInvariant().Replace('ı', 'i');
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    static readonly Regex Token = new(@"[\p{L}]+|\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "ve", "ile", "bir", "bu", "da", "de", "icin", "the", "and", "for", "to", "of", "a", "an", "in", "on", "is", "are", "be", "merhaba", "hello", "hi",
        "saygilarimla", "regards", "best", "thanks", "tesekkurler", "lutfen", "please", "re", "fw", "fwd", "ynt", "ilt"
    };

    public static float[] Embed(string text)
    {
        var v = new float[Dim];
        var words = Token.Matches(Normalize(text)).Select(m => m.Value).Where(w => !Stop.Contains(w)).ToList();
        void Add(string feature, float weight)
        {
            ulong h = 14695981039346656037;
            foreach (var c in feature) { h ^= c; h *= 1099511628211; }
            v[(int)(h % Dim)] += ((h >> 40) & 1) == 0 ? weight : -weight;
        }
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            Add("w:" + w, 1.0f);
            // a crude stem: the first five letters carry most of a Turkish / English word
            if (w.Length > 5 && char.IsLetter(w[0])) Add("s:" + w[..5], 0.8f);
            var padded = "<" + w + ">";
            for (var j = 0; j + 3 <= padded.Length; j++) Add("t:" + padded.Substring(j, 3), 0.35f);
            if (i + 1 < words.Count) Add("b:" + w + "_" + words[i + 1], 0.5f);
        }
        Normalize(v);
        return v;
    }

    internal static void Normalize(float[] v)
    {
        double n = 0; foreach (var x in v) n += x * x;
        if (n <= 0) return;
        var inv = (float)(1 / Math.Sqrt(n));
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
    }
}

/// <summary>Embeddings from a local Ollama server. Only a loopback address is accepted: mail text never leaves the PC.</summary>
public sealed class OllamaEmbedder : IEmbedder
{
    readonly HttpClient _http;
    public string Model { get; }
    public Uri Endpoint { get; }
    public string Id => "ollama:" + Model;

    public OllamaEmbedder(string model, string endpoint = "http://127.0.0.1:11434", HttpClient? http = null)
    {
        Model = model;
        Endpoint = new Uri(endpoint);
        if (!IsLoopback(Endpoint)) throw new InvalidOperationException(L.T("Anlamla arama yalnızca bu bilgisayardaki Ollama ile çalışır (127.0.0.1 / localhost)."));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public static bool IsLoopback(Uri u) => u.IsLoopback || u.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(new Uri(Endpoint, "/api/embed"), new { model = Model, input = texts, truncate = true }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(L.F("Ollama hata verdi ({0}): {1}", (int)resp.StatusCode, body.Length > 300 ? body[..300] : body));
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("embeddings");
        var result = new float[arr.GetArrayLength()][];
        var i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            var v = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            LocalNgramEmbedder.Normalize(v); result[i++] = v;
        }
        return result;
    }

    /// <summary>Models installed in the local Ollama (empty when Ollama is not running).</summary>
    public static async Task<List<string>> InstalledModelsAsync(string endpoint = "http://127.0.0.1:11434", CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var doc = JsonDocument.Parse(await http.GetStringAsync(new Uri(new Uri(endpoint), "/api/tags"), ct));
            return doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { return new(); }
    }

    /// <summary>True for models made for embeddings (better than a chat model's vectors).</summary>
    public static bool IsEmbeddingModel(string name) => Regex.IsMatch(name, @"embed|bge|e5|minilm|gte|nomic|mxbai|arctic|paraphrase|granite-embedding", RegexOptions.IgnoreCase);
}

/// <summary>One hit of a meaning search.</summary>
public sealed record SemanticHit(MailHeader Mail, double Score, string Snippet);

/// <summary>
/// Vector index of the local mail copy in its own SQLite file (semantic.db). Updating is incremental: a mail is
/// embedded again only when its text or the embedding model changed. Search keeps the vectors in memory as 8-bit
/// numbers (1 KB per mail for 1024 dimensions) and scores them against the query by dot product.
/// </summary>
public sealed class SemanticIndex : IDisposable
{
    readonly string _path;
    readonly IEmbedder _embedder;
    List<(string Key, sbyte[] Q)>? _mem;
    readonly object _lock = new();

    public SemanticIndex(string? path, IEmbedder embedder)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "semantic.db");
        _embedder = embedder;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS vec(key TEXT PRIMARY KEY, model TEXT, hash TEXT, dim INTEGER, v BLOB)";
        cmd.ExecuteNonQuery();
    }

    public string ModelId => _embedder.Id;

    SqliteConnection Open() { var c = new SqliteConnection($"Data Source={_path};Pooling=False"); c.Open(); return c; }

    static string Key(MailHeader m) => $"{m.AccountId}|{m.Folder}|{m.Uid}";

    /// <summary>The text of a mail that is embedded: subject, sender, attachment names and the start of the body.</summary>
    public static string DocumentText(MailHeader m, MailCache cache, int bodyChars = 1500)
    {
        var sb = new StringBuilder();
        sb.AppendLine(m.Subject).AppendLine(m.FromName + " " + m.FromAddress);
        if (m.HasAttachments) sb.AppendLine(string.Join(" ", cache.GetAttachments(m.AccountId, m.Folder, m.Uid).Select(a => Path.GetFileNameWithoutExtension(a.FileName).Replace('-', ' ').Replace('_', ' '))));
        var body = cache.GetBody(m.AccountId, m.Folder, m.Uid);
        var text = body != null && body.Text.Length > 0 ? body.Text : body != null && body.Html.Length > 0 ? MailService.HtmlToText(body.Html) : m.Preview;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        sb.Append(text.Length > bodyChars ? text[..bodyChars] : text);
        return sb.ToString();
    }

    static string Hash(string s)
    {
        ulong h = 14695981039346656037;
        foreach (var c in s) { h ^= c; h *= 1099511628211; }
        return h.ToString("x16");
    }

    public int Count
    {
        get { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM vec WHERE model=$m"; cmd.Parameters.AddWithValue("$m", _embedder.Id); return Convert.ToInt32(cmd.ExecuteScalar()); }
    }

    /// <summary>Adds new and changed mails, drops vanished ones. Reports (done, total). Returns the number embedded.</summary>
    public async Task<int> UpdateAsync(MailCache cache, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default, int batch = 32)
    {
        var mails = cache.GetAllHeaders(200000);
        var known = new Dictionary<string, string>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT key, hash FROM vec WHERE model=$m"; cmd.Parameters.AddWithValue("$m", _embedder.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) known[r.GetString(0)] = r.GetString(1);
        }
        var todo = new List<(string Key, string Hash, string Text)>();
        foreach (var m in mails)
        {
            var text = DocumentText(m, cache);
            var h = Hash(text);
            if (!known.TryGetValue(Key(m), out var old) || old != h) todo.Add((Key(m), h, text));
        }
        var alive = mails.Select(Key).ToHashSet();
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            // vectors of another model and of deleted mails go
            using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DELETE FROM vec WHERE model<>$m"; cmd.Parameters.AddWithValue("$m", _embedder.Id); cmd.ExecuteNonQuery(); }
            foreach (var gone in known.Keys.Where(k => !alive.Contains(k)))
            { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "DELETE FROM vec WHERE key=$k"; cmd.Parameters.AddWithValue("$k", gone); cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
        var done = 0;
        progress?.Report((0, todo.Count));
        for (var i = 0; i < todo.Count; i += batch)
        {
            ct.ThrowIfCancellationRequested();
            var part = todo.Skip(i).Take(batch).ToList();
            var vecs = await _embedder.EmbedAsync(part.Select(p => p.Text).ToList(), ct);
            using var c = Open();
            using var tx = c.BeginTransaction();
            for (var j = 0; j < part.Count; j++)
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO vec(key, model, hash, dim, v) VALUES($k, $m, $h, $d, $v)";
                cmd.Parameters.AddWithValue("$k", part[j].Key); cmd.Parameters.AddWithValue("$m", _embedder.Id); cmd.Parameters.AddWithValue("$h", part[j].Hash);
                cmd.Parameters.AddWithValue("$d", vecs[j].Length);
                var bytes = new byte[vecs[j].Length * 4]; Buffer.BlockCopy(vecs[j], 0, bytes, 0, bytes.Length);
                cmd.Parameters.AddWithValue("$v", bytes);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            done += part.Count;
            progress?.Report((done, todo.Count));
        }
        lock (_lock) _mem = null;
        return done;
    }

    static sbyte[] Quantize(float[] v)
    {
        var q = new sbyte[v.Length];
        var max = v.Length == 0 ? 1 : v.Max(x => Math.Abs(x)); if (max <= 0) max = 1;
        for (var i = 0; i < v.Length; i++) q[i] = (sbyte)Math.Clamp(Math.Round(v[i] / max * 127), -127, 127);
        return q;
    }

    List<(string Key, sbyte[] Q)> Memory()
    {
        lock (_lock)
        {
            if (_mem != null) return _mem;
            var list = new List<(string, sbyte[])>();
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key, v FROM vec WHERE model=$m"; cmd.Parameters.AddWithValue("$m", _embedder.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var bytes = (byte[])r["v"]; var v = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
                list.Add((r.GetString(0), Quantize(v)));
            }
            return _mem = list;
        }
    }

    /// <summary>The mails closest in meaning to <paramref name="query"/>, best first (scores 0..1).</summary>
    public async Task<List<SemanticHit>> SearchAsync(string query, MailCache cache, int top = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var qv = (await _embedder.EmbedAsync(new[] { query }, ct))[0];
        var qq = Quantize(qv); var qNorm = Math.Sqrt(qq.Sum(x => (double)x * x));
        var scored = new List<(string Key, double Score)>();
        foreach (var (key, v) in Memory())
        {
            if (v.Length != qq.Length) continue;
            long dot = 0; long nn = 0;
            for (var i = 0; i < v.Length; i++) { dot += v[i] * qq[i]; nn += v[i] * v[i]; }
            if (nn == 0 || qNorm == 0) continue;
            scored.Add((key, dot / (Math.Sqrt(nn) * qNorm)));
        }
        var hits = new List<SemanticHit>();
        var words = Regex.Matches(LocalNgramEmbedder.Normalize(query), @"[\p{L}\d]{3,}").Select(m => m.Value).ToList();
        foreach (var (key, score) in scored.OrderByDescending(s => s.Score).Take(top * 3))
        {
            var p = key.Split('|'); if (p.Length < 3 || !uint.TryParse(p[^1], out var uid)) continue;
            var folder = string.Join("|", p[1..^1]);
            var m = cache.GetHeader(p[0], folder, uid);
            if (m == null) continue;
            var text = DocumentText(m, cache, 600);
            // small bonus when the query words literally appear (keeps exact part numbers on top)
            var norm = LocalNgramEmbedder.Normalize(text);
            var bonus = words.Count == 0 ? 0 : 0.08 * words.Count(w => norm.Contains(w)) / words.Count;
            hits.Add(new SemanticHit(m, Math.Min(1, Math.Max(0, score) + bonus), Snippet(text, words)));
        }
        return hits.OrderByDescending(h => h.Score).Take(top).ToList();
    }

    static string Snippet(string text, List<string> words)
    {
        var flat = Regex.Replace(text, @"\s+", " ");
        var norm = LocalNgramEmbedder.Normalize(flat);
        var at = words.Select(w => norm.IndexOf(w, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 60);
        var s = flat.Substring(start, Math.Min(200, flat.Length - start));
        return (start > 0 ? "…" : "") + s + (start + 200 < flat.Length ? "…" : "");
    }

    public void Dispose() { }
}

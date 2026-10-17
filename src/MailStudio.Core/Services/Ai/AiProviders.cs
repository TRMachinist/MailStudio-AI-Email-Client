using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MailStudio.Services.Ai;

/// <summary>
/// AI providers. No API key is used: a local Ollama server or desktop/CLI tools installed
/// on the computer (Claude Code, Codex, Gemini CLI) are run in the background.
/// </summary>
public interface IAiProvider
{
    string Name { get; }
    string Badge { get; }
    /// <summary>Selectable models (empty = default).</summary>
    Task<IReadOnlyList<string>> GetModelsAsync();
    IAsyncEnumerable<string> StreamAsync(string prompt, string? model, CancellationToken ct, string? mcpServerExe = null);
    /// <summary>Does it support the MCP tools (mailbox access)?</summary>
    bool SupportsMcp { get; }
}

// ------------------------------------------------------------------ Ollama (yerel model)

public sealed class OllamaProvider : IAiProvider
{
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ModelLocks = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> _active = new();
    readonly CancellationTokenSource _stop = new();
    readonly Func<string> _baseUrl;
    readonly HttpClient _http;
    public OllamaProvider(Func<string> baseUrl, HttpClient? http = null) { _baseUrl = baseUrl; _http = http ?? SharedHttp; }

    public string Name => "Ollama";
    public string Badge => L.T("Yerel model · iş bitince bellekten çıkarılır");
    public bool SupportsMcp => false;
    public string? LastReleaseError { get; private set; }
    public event Action<string>? StatusChanged;
    void Notify(string status) { try { StatusChanged?.Invoke(status); } catch { } }

    async Task<IReadOnlyList<string>> ModelsAsync(string endpoint, CancellationToken ct)
    {
        var json = JsonNode.Parse(await _http.GetStringAsync(endpoint + "/api/tags", ct));
        return json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "")
            .Where(s => s.Length > 0).ToList() ?? new List<string>();
    }
    public async Task<IReadOnlyList<string>> GetModelsAsync()
    {
        try { return await ModelsAsync(_baseUrl().TrimEnd('/'), _stop.Token); }
        catch { return Array.Empty<string>(); }
    }

    public IAsyncEnumerable<string> StreamAsync(string prompt, string? model, CancellationToken ct, string? mcpServerExe = null) => StreamCoreAsync(prompt,model,null,ct);
    public IAsyncEnumerable<string> StreamImageAsync(string prompt,string? model,IReadOnlyList<string> images,CancellationToken ct) => StreamCoreAsync(prompt,model,images,ct);

    async IAsyncEnumerable<string> StreamCoreAsync(string prompt,string? model,IReadOnlyList<string>? images,[EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = linked.Token;
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _active[id] = finished;
        var endpoint = _baseUrl().TrimEnd('/');
        SemaphoreSlim? gate = null;
        var entered = false;
        var attempted = false;
        try
        {
            if (string.IsNullOrWhiteSpace(model))
                model = (await ModelsAsync(endpoint, ct)).FirstOrDefault() ?? throw new InvalidOperationException(L.T("Ollama'da yüklü model yok."));
            // Serialize this app's requests for the same model, so cleanup cannot unload a concurrent generation.
            gate = ModelLocks.GetOrAdd(endpoint + "\n" + model, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct); entered = true;
            LastReleaseError = null;
            Notify(L.F("{0} yükleniyor / yanıt hazırlanıyor...", model));
            var body = JsonSerializer.Serialize(new { model, prompt, stream = true, think = false, keep_alive = 0, images },new JsonSerializerOptions{DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull});
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/generate")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            attempted = true;
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(L.F("Ollama hata: {0} {1}", (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct)));
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var node = JsonNode.Parse(line);
                if (node?["error"]?.GetValue<string>() is { Length: > 0 } error) throw new InvalidOperationException("Ollama: " + error);
                var chunk = node?["response"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(chunk)) yield return chunk;
                if (node?["done"]?.GetValue<bool>() == true) yield break;
            }
            throw new IOException(L.T("Ollama yanıtı tamamlanmadan bağlantı kapandı."));
        }
        finally
        {
            if (attempted && model != null)
            {
                // Also covers cancellation and early stream disposal; the generation's token may already be cancelled.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    using var unload = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/generate")
                    { Content = new StringContent(JsonSerializer.Serialize(new { model, stream = false, keep_alive = 0 }), Encoding.UTF8, "application/json") };
                    using var result = await _http.SendAsync(unload, timeout.Token);
                    result.EnsureSuccessStatusCode();
                    Notify(L.F("{0}: kullanım bitti, model bellekten çıkarıldı.", model));
                }
                catch (Exception ex) { LastReleaseError = ex.Message; Notify(L.T("Model belleğini boşaltma tamamlanamadı: ") + ex.Message); }
            }
            if (entered) gate!.Release();
            finished.TrySetResult(); _active.TryRemove(id, out _);
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        var pending = _active.Values.Select(t => t.Task).ToArray();
        if (pending.Length > 0) await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(8));
    }
}

// ------------------------------------------------------------------ CLI based providers

/// <summary>Runs an AI CLI tool installed on the computer; the prompt is given on stdin, the answer streams from stdout.</summary>
public abstract class CliProvider : IAiProvider
{
    public abstract string Name { get; }
    public abstract string Badge { get; }
    protected abstract string ExeName { get; }
    protected abstract IEnumerable<string> BuildArgs(string? model, string? mcpExe);
    public virtual bool SupportsMcp => true;
    public virtual Task<IReadOnlyList<string>> GetModelsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>Filters a stdout line before it is shown to the user (for noise lines).</summary>
    protected virtual bool KeepLine(string line) => true;

    /// <summary>If the first line is an error message (e.g. the session expired) it is shown as an error instead of an answer.</summary>
    protected virtual bool IsErrorLine(string line) =>
        line.StartsWith("Failed to authenticate", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Invalid API key", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Please run /login", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("ERROR:", StringComparison.Ordinal);

    public static string? ResolveExe(string name)
    {
        var exts = new[] { ".exe", ".cmd", ".bat", "" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.AddRange(new[]
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, "AppData", "Roaming", "npm"),
            Path.Combine(home, "AppData", "Local", "Programs", "OpenAI", "Codex", "bin"),
        });
        foreach (var d in dirs)
            foreach (var e in exts)
            {
                var p = Path.Combine(d.Trim(), name + e);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    public bool IsInstalled => ResolveExe(ExeName) != null;

    /// <summary>Arguments of the sign-in command (null = not supported).</summary>
    public virtual string? LoginArgs => null;

    /// <summary>Starts the sign-in in a visible console window (a browser opens).</summary>
    public virtual void StartLogin()
    {
        var exe = ResolveExe(ExeName) ?? throw new InvalidOperationException(L.F("{0} bulunamadı.", ExeName));
        Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = L.F("/k title {0} oturum açma & set GEMINI_API_KEY=& set GOOGLE_API_KEY=& \"{1}\" {2}", Name, exe, LoginArgs),
            UseShellExecute = true,
            WorkingDirectory = AppPaths.AiWorkspace
        });
    }

    /// <summary>Turns CLI error output into a short, understandable message.</summary>
    protected virtual string Friendly(string err)
    {
        if (err.Contains("code: 404") || err.Contains("NOT_FOUND")) return L.F("{0}: seçilen model bulunamadı veya hesabın erişemiyor. Model kutusundan başka bir model seç (ör. varsayılan).", Name);
        if (err.Contains("code: 429") || err.Contains("RESOURCE_EXHAUSTED") || err.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return L.F("{0}: kota/limit doldu. Biraz sonra tekrar dene veya başka model seç.", Name);
        if (err.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || err.Contains("login", StringComparison.OrdinalIgnoreCase)) return L.F("{0}: oturum açık değil. Paneldeki \"Oturum aç\" düğmesine bas.", Name);
        if (err.Contains("not supported when using Codex")) return L.F("{0}: bu model ChatGPT hesabında desteklenmiyor; başka model seç.", Name);
        var lines = err.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("at ") && !l.Contains("Ripgrep") && !l.Contains("True color")).ToList();
        return $"{Name}: " + string.Join("\n", lines.TakeLast(3));
    }

    /// <summary>Prepares provider-specific configuration files before running.</summary>
    protected virtual void Prepare(string? mcpExe) { }
    protected virtual void ConfigureEnvironment(IDictionary<string, string?> env) { }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, string? model, [EnumeratorCancellation] CancellationToken ct, string? mcpServerExe = null)
    {
        Prepare(mcpServerExe);
        var exe = ResolveExe(ExeName) ?? throw new InvalidOperationException(L.F("'{0}' bulunamadı. {1} kurulu mu?", ExeName, Name));
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.AiWorkspace,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;
        foreach (var a in BuildArgs(model, mcpServerExe)) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        ConfigureEnvironment(psi.Environment);
        psi.Environment["FORCE_COLOR"] = "0";

        using var proc = new Process { StartInfo = psi };
        var channel = Channel.CreateUnbounded<string>();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) channel.Writer.TryWrite(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => Task.Run(async () => { await Task.Delay(150); channel.Writer.TryComplete(); });

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();

        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });

        bool any = false;
        await foreach (var line in channel.Reader.ReadAllAsync(ct))
        {
            if (!KeepLine(line)) continue;
            if (!any && IsErrorLine(line)) throw new InvalidOperationException(Friendly(line));
            any = true;
            yield return line + "\n";
        }

        if (!any)
        {
            var err = stderr.ToString().Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(err)
                ? L.F("{0} boş cevap döndü (çıkış kodu {1}).", Name, proc.ExitCode)
                : Friendly(err));
        }
    }

    static string LastLines(string s, int n) => string.Join("\n", s.Split('\n').TakeLast(n));
}

public sealed class ClaudeCliProvider : CliProvider
{
    public override string Name => "Claude";
    public override string? LoginArgs => "auth login";
    public override string Badge => L.T("Claude Code (masaüstü aboneliğin)");
    protected override string ExeName => "claude";
    protected override IEnumerable<string> BuildArgs(string? model, string? mcpExe)
    {
        yield return "-p";
        yield return "--output-format";
        yield return "text";
        if (!string.IsNullOrWhiteSpace(model)) { yield return "--model"; yield return model; }
        if (mcpExe != null)
        {
            yield return "--mcp-config";
            yield return McpConfigFile;
            yield return "--strict-mcp-config";
            yield return "--allowedTools";
            yield return "mcp__mailstudio";
        }
    }

    static string McpConfigFile => Path.Combine(AppPaths.AiWorkspace, "claude-mcp.json");

    protected override void Prepare(string? mcpExe)
    {
        if (mcpExe == null) return;
        File.WriteAllText(McpConfigFile, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object> { ["mailstudio"] = new { command = mcpExe, args = Array.Empty<string>() } }
        }));
    }
    public override Task<IReadOnlyList<string>> GetModelsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(new[] { "", "opus", "sonnet", "haiku" });
}

public sealed class CodexCliProvider : CliProvider
{
    public override string Name => "Codex";
    public override string? LoginArgs => "login";
    public override string Badge => L.T("OpenAI Codex (ChatGPT hesabın)");
    protected override string ExeName => "codex";
    protected override IEnumerable<string> BuildArgs(string? model, string? mcpExe)
    {
        yield return "exec";
        if (mcpExe != null)
        {
            // Define the MCP server only for this run without touching the persistent settings (TOML literal string)
            yield return "-c";
            yield return $"mcp_servers.mailstudio.command='{mcpExe}'";
        }
        yield return "--skip-git-repo-check";
        yield return "--color";
        yield return "never";
        yield return "-s";
        yield return "read-only";
        if (!string.IsNullOrWhiteSpace(model)) { yield return "-m"; yield return model; }
        yield return "-";
    }

    /// <summary>Codex'in kendi model listesini (~/.codex/models_cache.json) okur.</summary>
    public override Task<IReadOnlyList<string>> GetModelsAsync()
    {
        try
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
            var root = JsonNode.Parse(File.ReadAllText(f));
            var arr = (root?["models"] as JsonArray) ?? (root as JsonArray);
            var slugs = arr?.Select(m => m?["slug"]?.GetValue<string>() ?? "")
                           .Where(s => s.Length > 0 && !s.Contains("review")).Distinct().ToList();
            if (slugs is { Count: > 0 }) return Task.FromResult<IReadOnlyList<string>>(slugs);
        }
        catch { }
        return Task.FromResult<IReadOnlyList<string>>(new[] { "gpt-6-sol", "gpt-5.6-sol" });
    }
}

public sealed class GeminiCliProvider : CliProvider
{
    public override string Name => "Gemini";
    public override string Badge => L.T("Gemini (Google hesabınla giriş, API anahtarı yok)");
    protected override void ConfigureEnvironment(IDictionary<string, string?> env)
    {
        // If an API key is in the environment the CLI may prefer it; remove it to force the subscription sign-in.
        env.Remove("GEMINI_API_KEY");
        env.Remove("GOOGLE_API_KEY");
        env.Remove("GOOGLE_GENAI_USE_VERTEXAI");
    }
    protected override string ExeName => "gemini";
    protected override IEnumerable<string> BuildArgs(string? model, string? mcpExe)
    {
        if (!string.IsNullOrWhiteSpace(model)) { yield return "-m"; yield return model; }
        if (mcpExe != null) { yield return "--allowed-mcp-server-names"; yield return "mailstudio"; }
    }

    /// <summary>Gemini project settings: ai-workspace\.gemini\settings.json (only MailStudio's own working folder).</summary>
    protected override void Prepare(string? mcpExe)
    {
        var dir = Path.Combine(AppPaths.AiWorkspace, ".gemini");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        // NOT an API key: the Google account sign-in (subscription) is used. This setting applies only to the MailStudio working folder.
        var security = new { auth = new { selectedType = "oauth-personal" } };
        object cfg = mcpExe == null
            ? new { security, mcpServers = new Dictionary<string, object>() }
            : new { security, mcpServers = new Dictionary<string, object> { ["mailstudio"] = new { command = mcpExe, trust = true } } };
        File.WriteAllText(file, JsonSerializer.Serialize(cfg));
    }
    protected override bool KeepLine(string line) =>
        !line.StartsWith("Ripgrep is not available") && !line.StartsWith("Loaded cached credentials") &&
        !line.Contains("True color (24-bit)");
    public override Task<IReadOnlyList<string>> GetModelsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(new[] { "", "gemini-3.8-flash", "gemini-3.5-flash", "gemini-3.1-pro-preview", "gemini-3.5-flash-lite" });
    public override string? LoginArgs => "";
    public override void StartLogin() { Prepare(null); base.StartLogin(); }
}

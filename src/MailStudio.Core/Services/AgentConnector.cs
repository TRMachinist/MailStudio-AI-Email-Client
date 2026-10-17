using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailStudio.Services.Ai;

namespace MailStudio.Services;

/// <summary>Permission given to an AI agent connected over MCP. Sending mail is never possible.</summary>
public enum AgentPermission { Read, Draft, Organize }

public enum AgentConfigKind { McpServersJson, VsCodeJson, CodexToml, GooseYaml }

public sealed class AgentInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public AgentConfigKind Kind { get; init; }
    public bool Installed { get; set; }
    public bool Connected { get; set; }
    public bool PathOutdated { get; set; }
    public AgentPermission Permission { get; set; } = AgentPermission.Draft;
    public string Hint { get; init; } = "";
    /// <summary>Config path for display, with %APPDATA% / %USERPROFILE% instead of the user's folder.</summary>
    public string DisplayPath { get; init; } = "";
}

/// <summary>
/// One-click connection of MailStudio's MCP server to the AI agents people already use. Each agent's own config file
/// is edited in place: a backup is written first, only the "mailstudio" entry is added or removed, everything else
/// in the file is kept as it is.
/// </summary>
public static class AgentConnector
{
    public const string ServerName = "mailstudio";
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>For tests: redirects the home/app-data roots used to find config files.</summary>
    public static string? TestRoot { get; set; }

    public static List<AgentInfo> Detect(string? mcpExe = null)
    {
        mcpExe ??= McpLocator.Find();
        var list = new List<AgentInfo>
        {
            Make("claude-desktop", "Claude Desktop", ClaudeDesktopConfig(), AgentConfigKind.McpServersJson,
                Directory.Exists(Path.Combine(Roaming, "Claude")) || Directory.Exists(Path.Combine(Local, "AnthropicClaude")) || ClaudeDesktopMsix() != null,
                L.T("Bağladıktan sonra Claude Desktop'ı kapatıp yeniden açın.")),
            Make("claude-code", "Claude Code", Path.Combine(Home, ".claude.json"), AgentConfigKind.McpServersJson,
                File.Exists(Path.Combine(Home, ".claude.json")) || Directory.Exists(Path.Combine(Home, ".claude")) || CliProvider.ResolveExe("claude") != null,
                L.T("Yeni bir claude oturumunda /mcp ile görünür.")),
            Make("codex", "OpenAI Codex", Path.Combine(Home, ".codex", "config.toml"), AgentConfigKind.CodexToml,
                Directory.Exists(Path.Combine(Home, ".codex")) || CliProvider.ResolveExe("codex") != null,
                L.T("Yeni bir codex oturumunda kullanılabilir.")),
            Make("gemini", "Gemini CLI", Path.Combine(Home, ".gemini", "settings.json"), AgentConfigKind.McpServersJson,
                Directory.Exists(Path.Combine(Home, ".gemini")) || CliProvider.ResolveExe("gemini") != null,
                L.T("Yeni bir gemini oturumunda /mcp ile görünür.")),
            Make("cursor", "Cursor", Path.Combine(Home, ".cursor", "mcp.json"), AgentConfigKind.McpServersJson,
                Directory.Exists(Path.Combine(Home, ".cursor")) || Directory.Exists(Path.Combine(Local, "Programs", "cursor")),
                L.T("Cursor ayarlarında MCP bölümünden etkinleştirin.")),
            Make("vscode", "VS Code (Copilot)", Path.Combine(Roaming, "Code", "User", "mcp.json"), AgentConfigKind.VsCodeJson,
                Directory.Exists(Path.Combine(Roaming, "Code")),
                L.T("Copilot sohbetinde Agent modunda araçlar listesinde görünür.")),
            Make("windsurf", "Windsurf", Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json"), AgentConfigKind.McpServersJson,
                Directory.Exists(Path.Combine(Home, ".codeium", "windsurf")),
                L.T("Windsurf'ü yeniden başlatın.")),
            Make("lmstudio", "LM Studio", Path.Combine(Home, ".lmstudio", "mcp.json"), AgentConfigKind.McpServersJson,
                Directory.Exists(Path.Combine(Home, ".lmstudio")),
                L.T("LM Studio'da Program > MCP bölümünden açın; yerel modellerle çalışır.")),
            Make("goose", "Goose", Path.Combine(Roaming, "Block", "goose", "config", "config.yaml"), AgentConfigKind.GooseYaml,
                Directory.Exists(Path.Combine(Roaming, "Block", "goose")) || CliProvider.ResolveExe("goose") != null,
                L.T("Goose'u yeniden başlatın.")),
        };
        foreach (var a in list) Read(a, mcpExe);
        return list;
    }

    static AgentInfo Make(string id, string name, string path, AgentConfigKind kind, bool installed, string hint) =>
        new() { Id = id, Name = name, ConfigPath = TestRoot == null ? path : Path.Combine(TestRoot, id, Path.GetFileName(path)), Kind = kind,
                Installed = TestRoot != null || installed, Hint = hint, DisplayPath = Shorten(path) };

    /// <summary>Replaces the user's own folders with environment variables (no user name on screen or in screenshots).</summary>
    public static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var map = new List<(string, string)>();
        if (TestRoot != null) map.Add((TestRoot, "%TEST%"));
        map.Add((Roaming, "%APPDATA%")); map.Add((Local, "%LOCALAPPDATA%")); map.Add((Home, "%USERPROFILE%"));
        foreach (var (from, to) in map.Where(m => m.Item1.Length > 3).OrderByDescending(m => m.Item1.Length))
            text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    static string? ClaudeDesktopMsix()
    {
        try
        {
            var pk = Path.Combine(Local, "Packages");
            if (!Directory.Exists(pk)) return null;
            var d = Directory.GetDirectories(pk, "Claude_*").FirstOrDefault();
            return d == null ? null : Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
        }
        catch { return null; }
    }

    static string ClaudeDesktopConfig()
    {
        var msix = ClaudeDesktopMsix();
        var classic = Path.Combine(Roaming, "Claude", "claude_desktop_config.json");
        return msix != null && (File.Exists(msix) || !Directory.Exists(Path.Combine(Roaming, "Claude"))) ? msix : classic;
    }

    static string[] Args(AgentPermission p) => new[] { "--permission", p.ToString().ToLowerInvariant() };

    static AgentPermission ParsePermission(IEnumerable<string> args)
    {
        var a = args.ToList(); var i = a.IndexOf("--permission");
        return i >= 0 && i + 1 < a.Count && Enum.TryParse<AgentPermission>(a[i + 1], true, out var p) ? p : AgentPermission.Draft;
    }

    static void Read(AgentInfo a, string? mcpExe)
    {
        try
        {
            if (!File.Exists(a.ConfigPath)) return;
            var text = File.ReadAllText(a.ConfigPath);
            string? command = null; IEnumerable<string> args = Array.Empty<string>();
            switch (a.Kind)
            {
                case AgentConfigKind.McpServersJson:
                case AgentConfigKind.VsCodeJson:
                    var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var entry = root?[a.Kind == AgentConfigKind.VsCodeJson ? "servers" : "mcpServers"]?[ServerName];
                    if (entry == null) return;
                    command = entry["command"]?.GetValue<string>();
                    args = entry["args"]?.AsArray().Select(x => x?.GetValue<string>() ?? "") ?? args;
                    break;
                case AgentConfigKind.CodexToml:
                    var sec = TomlSection(text);
                    if (sec == null) return;
                    command = System.Text.RegularExpressions.Regex.Match(sec, @"command\s*=\s*['""](.+?)['""]").Groups[1].Value;
                    args = System.Text.RegularExpressions.Regex.Matches(sec, @"args\s*=\s*\[(.*?)\]").SelectMany(m => m.Groups[1].Value.Split(',')).Select(x => x.Trim().Trim('"', '\''));
                    break;
                case AgentConfigKind.GooseYaml:
                    var block = GooseBlock(text);
                    if (block == null) return;
                    command = System.Text.RegularExpressions.Regex.Match(block, @"cmd:\s*['""]?(.+?)['""]?\s*$", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
                    args = System.Text.RegularExpressions.Regex.Match(block, @"args:\s*\[(.*?)\]").Groups[1].Value.Split(',').Select(x => x.Trim().Trim('"', '\''));
                    break;
            }
            a.Connected = !string.IsNullOrEmpty(command);
            a.Permission = ParsePermission(args);
            a.PathOutdated = a.Connected && (mcpExe == null || !string.Equals(Path.GetFullPath(command!), Path.GetFullPath(mcpExe), StringComparison.OrdinalIgnoreCase));
        }
        catch { /* unreadable config: shown as not connected */ }
    }

    /// <summary>Adds or updates the "mailstudio" entry. Returns the backup file (null if the config did not exist).</summary>
    public static string? Connect(AgentInfo a, string mcpExe, AgentPermission permission)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(a.ConfigPath)!);
        var exists = File.Exists(a.ConfigPath);
        var text = exists ? File.ReadAllText(a.ConfigPath) : "";
        var backup = exists ? Backup(a.ConfigPath) : null;
        string updated = a.Kind switch
        {
            AgentConfigKind.McpServersJson => JsonEntry(text, "mcpServers", new JsonObject { ["command"] = mcpExe, ["args"] = new JsonArray(Args(permission).Select(x => (JsonNode)x!).ToArray()) }),
            AgentConfigKind.VsCodeJson => JsonEntry(text, "servers", new JsonObject { ["type"] = "stdio", ["command"] = mcpExe, ["args"] = new JsonArray(Args(permission).Select(x => (JsonNode)x!).ToArray()) }),
            AgentConfigKind.CodexToml => TomlEntry(text, mcpExe, permission),
            AgentConfigKind.GooseYaml => GooseEntry(text, mcpExe, permission),
            _ => throw new NotSupportedException(),
        };
        WriteAtomic(a.ConfigPath, updated);
        a.Connected = true; a.Permission = permission; a.PathOutdated = false;
        return backup;
    }

    public static void Disconnect(AgentInfo a)
    {
        if (!File.Exists(a.ConfigPath)) { a.Connected = false; return; }
        var text = File.ReadAllText(a.ConfigPath);
        Backup(a.ConfigPath);
        string updated = a.Kind switch
        {
            AgentConfigKind.McpServersJson => JsonEntry(text, "mcpServers", null),
            AgentConfigKind.VsCodeJson => JsonEntry(text, "servers", null),
            AgentConfigKind.CodexToml => RemoveToml(text),
            AgentConfigKind.GooseYaml => RemoveGoose(text),
            _ => text,
        };
        WriteAtomic(a.ConfigPath, updated);
        a.Connected = false;
    }

    static string Backup(string path)
    {
        var b = path + ".mailstudio-backup";
        File.Copy(path, b, true);
        return b;
    }

    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".mailstudio-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    static string JsonEntry(string text, string section, JsonObject? entry)
    {
        var root = string.IsNullOrWhiteSpace(text) ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException(L.T("Ayar dosyası bir JSON nesnesi değil."));
        if (root[section] is not JsonObject servers) { if (entry == null) return text; servers = new JsonObject(); root[section] = servers; }
        if (entry == null) servers.Remove(ServerName); else servers[ServerName] = entry;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    const string TomlHeader = "[mcp_servers.mailstudio]";

    static string? TomlSection(string text)
    {
        var i = text.IndexOf(TomlHeader, StringComparison.Ordinal);
        if (i < 0) return null;
        var next = text.IndexOf("\n[", i + TomlHeader.Length, StringComparison.Ordinal);
        return next < 0 ? text[i..] : text[i..next];
    }

    static string RemoveToml(string text)
    {
        var sec = TomlSection(text);
        return sec == null ? text : text.Replace(sec, "").TrimEnd() + "\n";
    }

    static string TomlEntry(string text, string exe, AgentPermission p)
    {
        var block = $"{TomlHeader}\ncommand = '{exe}'\nargs = [{string.Join(", ", Args(p).Select(x => $"\"{x}\""))}]\nstartup_timeout_sec = 30\n";
        var rest = RemoveToml(text.Replace("\r\n", "\n")).TrimEnd();
        return (rest.Length > 0 ? rest + "\n\n" : "") + block;
    }

    static string? GooseBlock(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "  mailstudio:");
        if (start < 0) return null;
        var end = start + 1;
        while (end < lines.Length && (lines[end].StartsWith("    ") || lines[end].Trim().Length == 0)) end++;
        return string.Join("\n", lines[start..end]);
    }

    static string RemoveGoose(string text)
    {
        var b = GooseBlock(text);
        return b == null ? text : text.Replace("\r\n", "\n").Replace(b + "\n", "").Replace(b, "");
    }

    static string GooseEntry(string text, string exe, AgentPermission p)
    {
        var block = "  mailstudio:\n    enabled: true\n    name: mailstudio\n    type: stdio\n" +
                    $"    cmd: '{exe}'\n    args: [{string.Join(", ", Args(p))}]\n    envs: {{}}\n    timeout: 300\n";
        var t = RemoveGoose(text.Replace("\r\n", "\n"));
        var lines = t.Split('\n').ToList();
        var ext = lines.FindIndex(l => l.TrimEnd() == "extensions:");
        if (ext < 0) return t.TrimEnd() + (t.Trim().Length > 0 ? "\n" : "") + "extensions:\n" + block;
        lines.Insert(ext + 1, block.TrimEnd('\n'));
        return string.Join("\n", lines);
    }

    /// <summary>Starts the MCP server like an agent would and asks for its tool list.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(string mcpExe, AgentPermission permission, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(mcpExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in Args(permission)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("MCP");
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            async Task Send(string json) { await p.StandardInput.WriteLineAsync(json); await p.StandardInput.FlushAsync(); }
            async Task<JsonElement> Receive(int id)
            {
                while (true)
                {
                    var line = await p.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException(L.T("MCP sunucusu kapandı."));
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id) return doc.RootElement.Clone();
                }
            }
            await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mailstudio-test","version":"1"}}}""");
            await Receive(1);
            await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            var r = await Receive(2);
            var tools = r.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            if (tools.Any(t => t is "send" or "send_message" or "send_mail")) return (false, "send tool exposed");
            return (true, L.F("Çalışıyor: {0} araç ({1}).", tools.Count, string.Join(", ", tools)));
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { p.Kill(true); } catch { } }
    }
}

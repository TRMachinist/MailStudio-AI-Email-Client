using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailStudio.Models;

namespace MailStudio.Services;

public static class AppPaths
{
    // An explicit alternate data directory enables isolated development/tests without touching the user's mailbox.
    public static string Root { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("MAILSTUDIO_DATA_DIR") is { Length: > 0 } alternate
            ? alternate : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailStudio"));
    public static string AccountsFile => Path.Combine(Root, "accounts.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string CacheDb => Path.Combine(Root, "cache.db");
    public static string AiWorkspace => Path.Combine(Root, "ai-workspace");
    public static string Attachments => Path.Combine(Root, "attachments");

    static AppPaths()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AiWorkspace);
        Directory.CreateDirectory(Attachments);
    }
}

/// <summary>Stores accounts as JSON; passwords are encrypted with Windows DPAPI (CurrentUser).</summary>
public class AccountStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<AccountConfig> Accounts { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(AppPaths.AccountsFile)) { Accounts = new(); return; }
        Accounts = JsonSerializer.Deserialize<List<AccountConfig>>(File.ReadAllText(AppPaths.AccountsFile), Json) ?? new();
    }

    public void Save() => File.WriteAllText(AppPaths.AccountsFile, JsonSerializer.Serialize(Accounts, Json));

    public void AddOrUpdate(AccountConfig acc)
    {
        var i = Accounts.FindIndex(a => a.Id == acc.Id);
        if (i >= 0) Accounts[i] = acc; else Accounts.Add(acc);
        Save();
    }

    public void Remove(string id) { Accounts.RemoveAll(a => a.Id == id); Save(); }

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
    }
}

public class AppSettings
{
    public string AiProvider { get; set; } = "Ollama";
    public string OllamaModel { get; set; } = "qwen3.5:4b";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string ClaudeModel { get; set; } = "";
    public string CodexModel { get; set; } = "";
    public string GeminiModel { get; set; } = "";
    public bool BlockRemoteImages { get; set; } = true;
    public bool ShowAiPane { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 5;
    public bool DesktopAlerts { get; set; } = true;
    public bool ConversationView { get; set; } = true;
    public bool AiMailboxAccess { get; set; } = false;
    /// <summary>System | Light | Dark</summary>
    public string Theme { get; set; } = "Light";
    public bool ShowFolderPane { get; set; } = true;
    public double FolderPaneWidth { get; set; } = 230;
    public double ListPaneWidth { get; set; } = 380;
    public double AiPaneWidth { get; set; } = 340;
    public double WindowWidth { get; set; } = 0;
    public double WindowHeight { get; set; } = 0;
    public bool WindowMaximized { get; set; } = false;
    /// <summary>CAD scene: Dark (dark studio) | Light (technical light) | Theme (same as the app theme)</summary>
    public string CadScene { get; set; } = "Theme";
    public string LastMaterial { get; set; } = "Al 7075-T6 / T7351";
    /// <summary>Is the AI panel open in the PDF window (default off: the page opens larger).</summary>
    public bool PdfSidePanel { get; set; }
    public List<MailStudio.Models.FormatRule> FormatRules { get; set; } = new();
    /// <summary>Skills the user switched off (folder names).</summary>
    public List<string> DisabledSkills { get; set; } = new();
    /// <summary>Skills that run by themselves when new mail arrives (opt-in).</summary>
    public List<string> AutoRunSkills { get; set; } = new();
    /// <summary>UI language: auto (Windows language), tr or en.</summary>
    public string Language { get; set; } = "auto";
    public string MicrosoftClientId { get; set; } = "";
    // Google OAuth client: the builder's own registration lives in OAuthDefaults.Local.cs (not in the repository);
    // without it the value is empty and the user enters their own client ID in the account settings (docs/OAUTH_SETUP.md).
    public string GoogleClientId { get; set; } = OAuthDefaults.GoogleClientId;
    public string GoogleClientSecret { get; set; } = OAuthDefaults.GoogleClientSecret;

    static AppSettings? _shared;
    /// <summary>Single application-wide settings object; windows change the same instance.</summary>
    public static AppSettings Shared => _shared ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save() =>
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

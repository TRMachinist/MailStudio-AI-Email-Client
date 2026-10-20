using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using MailStudio.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace MailStudio.Services;

/// <summary>
/// Provides XOAUTH2 access tokens for Outlook.com / Microsoft 365 (MSAL) and Gmail (Google OAuth).
/// Tokens are kept in an encrypted local cache; they are renewed silently when expired, a browser opens if needed.
/// </summary>
public class OAuthService
{
    static readonly string[] MsScopes =
    {
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
        "offline_access"
    };
    static readonly string[] GoogleScopes = { "https://mail.google.com/" };
    // Calendar access is a separate, opt-in consent: it is only requested when the user turns calendar sync on.
    // (Microsoft refuses one token for two resources, so Graph gets its own token request.)
    static readonly string[] MsCalendarScopes = { "https://graph.microsoft.com/Calendars.ReadWrite" };
    static readonly string[] GoogleCalendarScopes = { "https://www.googleapis.com/auth/calendar" };

    readonly Func<AppSettings> _settings;
    IPublicClientApplication? _msal;
    string? _msalClientId;
    readonly SemaphoreSlim _lock = new(1, 1);

    public OAuthService(Func<AppSettings> settings) => _settings = settings;

    /// <summary>Throws if interactive sign-in is not allowed (background / MCP) and there is no token.</summary>
    public bool AllowInteractive { get; set; } = true;

    public virtual async Task<string> GetAccessTokenAsync(AccountConfig a, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return a.AuthType switch
            {
                AuthType.Microsoft => await MicrosoftTokenAsync(a.Email, ct),
                AuthType.Google => await GoogleTokenAsync(a.Email, ct),
                _ => throw new InvalidOperationException(L.T("Bu hesap OAuth kullanmıyor."))
            };
        }
        finally { _lock.Release(); }
    }

    /// <summary>Token for the calendar API (Microsoft Graph / Google Calendar). Separate consent from the mail token.</summary>
    public virtual async Task<string> GetCalendarAccessTokenAsync(AccountConfig a, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return a.AuthType switch
            {
                AuthType.Microsoft => await MicrosoftTokenAsync(a.Email, ct, MsCalendarScopes),
                AuthType.Google => await GoogleTokenAsync(a.Email, ct, GoogleCalendarScopes, a.Email + "#calendar"),
                _ => throw new InvalidOperationException(L.T("Bu hesap OAuth kullanmıyor."))
            };
        }
        finally { _lock.Release(); }
    }

    // ---------------------------------------------------------------- Microsoft

    async Task<IPublicClientApplication> MsalAsync()
    {
        var clientId = _settings().MicrosoftClientId?.Trim();
        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException(L.T("Microsoft uygulama (istemci) kimliği girilmemiş. Hesap penceresindeki 'OAuth ayarları' bölümüne bak."));
        if (_msal != null && _msalClientId == clientId) return _msal;

        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithRedirectUri("http://localhost")
            .Build();

        var props = new StorageCreationPropertiesBuilder("msal.cache", AppPaths.Root).Build();
        var helper = await MsalCacheHelper.CreateAsync(props);
        helper.RegisterCache(app.UserTokenCache);
        _msal = app;
        _msalClientId = clientId;
        return app;
    }

    async Task<string> MicrosoftTokenAsync(string email, CancellationToken ct, string[]? scopes = null)
    {
        scopes ??= MsScopes;
        var app = await MsalAsync();
        var accounts = await app.GetAccountsAsync();
        var acc = accounts.FirstOrDefault(x => string.Equals(x.Username, email, StringComparison.OrdinalIgnoreCase));
        if (acc != null)
        {
            try { return (await app.AcquireTokenSilent(scopes, acc).ExecuteAsync(ct)).AccessToken; }
            catch (MsalUiRequiredException) { }
        }
        if (!AllowInteractive)
            throw new InvalidOperationException(L.F("{0} için Microsoft oturumu gerekli. MailStudio'yu açıp hesaba bir kez giriş yap.", email));

        var r = await app.AcquireTokenInteractive(scopes)
            .WithLoginHint(email)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(ct);
        return r.AccessToken;
    }

    // ---------------------------------------------------------------- Google

    async Task<string> GoogleTokenAsync(string email, CancellationToken ct, string[]? scopes = null, string? tokenKey = null)
    {
        scopes ??= GoogleScopes;
        tokenKey ??= email;
        var s = _settings();
        if (string.IsNullOrWhiteSpace(s.GoogleClientId) || string.IsNullOrWhiteSpace(s.GoogleClientSecret))
            throw new InvalidOperationException(L.T("Google OAuth istemci kimliği/gizli anahtarı girilmemiş. Gmail için uygulama şifresi de kullanabilirsin."));

        var store = new FileDataStore(Path.Combine(AppPaths.Root, "google-tokens"), true);
        if (!AllowInteractive && await store.GetAsync<Google.Apis.Auth.OAuth2.Responses.TokenResponse>(tokenKey) == null)
            throw new InvalidOperationException(L.F("{0} için Google oturumu gerekli. MailStudio'yu açıp hesaba bir kez giriş yap.", email));

        var cred = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = s.GoogleClientId.Trim(), ClientSecret = s.GoogleClientSecret.Trim() },
            scopes, tokenKey, ct, store);
        if (cred.Token.IsStale) await cred.RefreshTokenAsync(ct);
        return cred.Token.AccessToken;
    }

    public async Task SignOutAsync(AccountConfig a)
    {
        if (a.AuthType == AuthType.Microsoft && _msal != null)
        {
            foreach (var acc in (await _msal.GetAccountsAsync()).Where(x => string.Equals(x.Username, a.Email, StringComparison.OrdinalIgnoreCase)))
                await _msal.RemoveAsync(acc);
        }
        if (a.AuthType == AuthType.Google)
            await new FileDataStore(Path.Combine(AppPaths.Root, "google-tokens"), true).DeleteAsync<object>(a.Email);
        if (a.AuthType == AuthType.Google)
            await new FileDataStore(Path.Combine(AppPaths.Root, "google-tokens"), true).DeleteAsync<object>(a.Email + "#calendar");
    }
}

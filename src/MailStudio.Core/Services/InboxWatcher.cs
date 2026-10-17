using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// Listens to the Inbox in IDLE mode with a separate IMAP connection per account.
/// When the server reports new mail the <see cref="NewMail"/> event is raised.
/// Stops silently on servers without IDLE (the app's periodic sync continues).
/// </summary>
public sealed class InboxWatcher : IDisposable
{
    readonly AccountConfig _acc;
    readonly OAuthService _oauth;
    readonly CancellationTokenSource _stop = new();
    Task? _loop;

    public event Action<string>? NewMail;            // accountId
    public event Action<string, string>? StatusChanged; // accountId, mesaj

    public InboxWatcher(AccountConfig acc, OAuthService oauth)
    {
        _acc = acc;
        _oauth = oauth;
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    async Task LoopAsync(CancellationToken stop)
    {
        var backoff = TimeSpan.FromSeconds(10);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = new ImapClient { Timeout = 120000 };
                await client.ConnectAsync(_acc.ImapHost, _acc.ImapPort,
                    _acc.ImapSecurity == SecurityMode.SslOnConnect ? SecureSocketOptions.SslOnConnect :
                    _acc.ImapSecurity == SecurityMode.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, stop);

                if (_acc.AuthType == AuthType.Password)
                    await client.AuthenticateAsync(string.IsNullOrWhiteSpace(_acc.UserName) ? _acc.Email : _acc.UserName,
                                                   AccountStore.Unprotect(_acc.ProtectedPassword), stop);
                else
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(_acc.Email, await _oauth.GetAccessTokenAsync(_acc, stop)), stop);

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    StatusChanged?.Invoke(_acc.Id, L.T("Sunucu IDLE desteklemiyor; periyodik kontrol kullanılıyor."));
                    return;
                }

                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, stop);
                int lastCount = inbox.Count;
                backoff = TimeSpan.FromSeconds(10);
                StatusChanged?.Invoke(_acc.Id, L.T("Anlık bildirim etkin"));

                while (!stop.IsCancellationRequested)
                {
                    using var done = new CancellationTokenSource(TimeSpan.FromMinutes(9)); // RFC: renew before 29 min
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    bool arrived = false;
                    void OnCount(object? s, EventArgs e)
                    {
                        if (inbox.Count > lastCount) { arrived = true; try { done.Cancel(); } catch { } }
                        lastCount = inbox.Count;
                    }
                    inbox.CountChanged += OnCount;
                    try { await client.IdleAsync(done.Token, stop); }
                    finally { inbox.CountChanged -= OnCount; }

                    if (arrived) NewMail?.Invoke(_acc.Id);
                    else await client.NoOpAsync(stop);
                }
                await client.DisconnectAsync(true, CancellationToken.None);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(_acc.Id, L.T("Anlık bağlantı koptu: ") + ex.Message);
                try { await Task.Delay(backoff, stop); } catch { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(300, backoff.TotalSeconds * 2));
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

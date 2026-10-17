using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace MailStudio.Services;

/// <summary>One application per data directory; launching again restores the running window.</summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly EventWaitHandle _show;
    readonly bool _ownsMutex;
    RegisteredWaitHandle? _wait;
    public bool IsPrimary => _ownsMutex;

    public SingleInstance(string dataDirectory)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..24];
        _mutex = new Mutex(true, @"Local\MailStudio-" + suffix, out _ownsMutex);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MailStudio-Show-" + suffix);
    }
    public void Listen(Dispatcher dispatcher, Action show)
    {
        if (!_ownsMutex) throw new InvalidOperationException("Only the primary instance can listen.");
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) =>
        {
            if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(show);
        }, null, Timeout.Infinite, false);
    }
    public void RestorePrimary() => _show.Set();
    public void Dispose()
    {
        _wait?.Unregister(null); _show.Dispose();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

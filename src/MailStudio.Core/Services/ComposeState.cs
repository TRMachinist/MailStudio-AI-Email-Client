namespace MailStudio.Services;

/// <summary>One operation gate shared by keyboard, buttons, saving and closing.</summary>
public sealed class ComposeState
{
    int _busy;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public long Revision { get; private set; }
    public long SavedRevision { get; private set; }
    public bool IsDirty => Revision != SavedRevision;
    public void Changed() => Revision++;
    public void Saved(long revision) => SavedRevision = revision;
    public bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    public void End() => Interlocked.Exchange(ref _busy, 0);
}

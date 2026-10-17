using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace MailStudio.Services.Ai;

/// <summary>
/// Runs the provider stream on a background thread and returns the chunks joined together.
/// Starting the process, writing a long prompt to stdin and per-token UI updates do not block the UI thread.
/// </summary>
public static class AiStream
{
    public static async IAsyncEnumerable<string> Background(Func<CancellationToken, IAsyncEnumerable<string>> source,
        [EnumeratorCancellation] CancellationToken ct, int intervalMs = 70)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in source(ct).WithCancellation(ct).ConfigureAwait(false))
                    channel.Writer.TryWrite(chunk);
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
        }, CancellationToken.None);
        var sb = new StringBuilder();
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                sb.Clear();
                while (channel.Reader.TryRead(out var chunk)) sb.Append(chunk);
                if (sb.Length > 0) yield return sb.ToString();
                await Task.Delay(intervalMs, ct);
            }
        }
        finally { try { await producer.ConfigureAwait(false); } catch { } }
    }

    /// <summary>One-shot complete answer; the calling thread does not wait.</summary>
    public static async Task<string> CompleteAsync(Func<CancellationToken, IAsyncEnumerable<string>> source, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var part in Background(source, ct)) sb.Append(part);
        return sb.ToString();
    }
}

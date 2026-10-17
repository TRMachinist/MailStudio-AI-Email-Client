using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MailStudio.Regression;

// Loopback-only synthetic protocol fixtures; no credentials or real mail are used.
sealed class FakeImap : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly List<Task> _connections = new();
    readonly Task _accept;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool UidPlus { get; set; } = true;
    public bool Move { get; set; } = true;
    public bool Trash { get; set; } = true;
    public bool FailAppend { get; set; }
    public bool LoseAppendResponse { get; set; }
    public uint Validity { get; set; } = 77;
    public int Appends;
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentDictionary<uint, string> Messages { get; } = new();
    public HashSet<uint> Deleted { get; } = new() { 1 };
    string Capabilities => "IMAP4rev1 NAMESPACE UNSELECT SPECIAL-USE" + (UidPlus ? " UIDPLUS" : "") + (Move ? " MOVE" : "");

    public FakeImap()
    {
        Messages[1] = "Message-Id: <one@test>\r\nSubject: Old\r\n\r\nold";
        Messages[2] = "Message-Id: <two@test>\r\nSubject: Selected\r\n\r\nselected";
        _listener.Start();
        _accept = Task.Run(AcceptAsync);
    }
    async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }
    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { NewLine = "\r\n", AutoFlush = true };
            Task Write(string value) => writer.WriteLineAsync(value);
            try
            {
                await Write("* OK [CAPABILITY " + Capabilities + "] synthetic server");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var space = line.IndexOf(' ');
                    if (space < 0) throw new InvalidDataException(line);
                    var tag = line[..space]; var cmd = line[(space + 1)..];
                    Commands.Enqueue(cmd.StartsWith("LOGIN ") ? "LOGIN [synthetic]" : cmd);
                    var upper = cmd.ToUpperInvariant();
                    if (upper == "CAPABILITY") await Write("* CAPABILITY " + Capabilities);
                    else if (upper.StartsWith("LOGIN ")) { }
                    else if (upper.StartsWith("AUTHENTICATE "))
                    { await Write("+ "); await reader.ReadLineAsync(_stop.Token); }
                    else if (upper == "NAMESPACE") await Write("* NAMESPACE ((\"\" \"/\")) NIL NIL");
                    else if (upper.StartsWith("LIST ") || upper.StartsWith("LSUB "))
                    {
                        var names = new List<(string flags, string name)> { ("\\Inbox", "INBOX"), ("\\Drafts", "Drafts"), ("\\Sent", "Sent") };
                        if (Trash) names.Add(("\\Trash", "Trash"));
                        foreach (var (flags, name) in names) await Write($"* LIST ({flags}) \"/\" \"{name}\"");
                    }
                    else if (upper.StartsWith("SELECT ") || upper.StartsWith("EXAMINE "))
                    {
                        await Write("* FLAGS (\\Seen \\Deleted \\Draft \\Flagged)");
                        await Write("* OK [PERMANENTFLAGS (\\Seen \\Deleted \\Draft \\Flagged)] flags");
                        await Write($"* {Messages.Count} EXISTS");
                        await Write($"* OK [UIDVALIDITY {Validity}] validity");
                        await Write("* OK [UIDNEXT 100] next");
                        await Write(tag + " OK [READ-WRITE] selected"); continue;
                    }
                    else if (upper.StartsWith("UID SEARCH "))
                    {
                        var match = Regex.Match(cmd, "HEADER [\\\"]?X-MailStudio-Draft-Id[\\\"]? [\\\"]?([a-f0-9]+)", RegexOptions.IgnoreCase);
                        var uids = match.Success ? Messages.Where(m => m.Value.Contains(match.Groups[1].Value)).Select(m => m.Key)
                            : upper.Contains("DELETED") ? Deleted : Messages.Keys;
                        await Write("* SEARCH " + string.Join(" ", uids.Order()));
                    }
                    else if (upper.StartsWith("UID FETCH ") && upper.Contains("BODY.PEEK[]"))
                    {
                        var uid = uint.Parse(cmd.Split(' ')[2]);
                        if (Messages.TryGetValue(uid, out var mime))
                        {
                            await Write($"* 1 FETCH (UID {uid} BODY[] {{{Encoding.UTF8.GetByteCount(mime)}}}");
                            await writer.WriteAsync(mime); await Write(")");
                        }
                    }
                    else if (upper.StartsWith("UID STORE "))
                    {
                        var uidSet = cmd.Split(' ')[2];
                        foreach (var uid in ParseSet(uidSet))
                        {
                            if (upper.Contains("+FLAGS") && upper.Contains("\\DELETED")) Deleted.Add(uid);
                            if (upper.Contains("-FLAGS") && upper.Contains("\\DELETED")) Deleted.Remove(uid);
                        }
                    }
                    else if (upper.StartsWith("UID EXPUNGE "))
                    {
                        foreach (var uid in ParseSet(cmd.Split(' ')[2]))
                            if (Deleted.Remove(uid)) Messages.TryRemove(uid, out _);
                    }
                    else if (upper.StartsWith("UID MOVE "))
                    {
                        Messages.TryRemove(uint.Parse(cmd.Split(' ')[2]), out _);
                        await Write(tag + " OK [COPYUID 77 2 102] moved"); continue;
                    }
                    else if (upper.StartsWith("UID COPY ")) { }
                    else if (upper.StartsWith("APPEND "))
                    {
                        var size = int.Parse(Regex.Match(cmd, @"\{(\d+)\+?\}$").Groups[1].Value);
                        if (!cmd.EndsWith("+}")) await Write("+ ready");
                        var data = new char[size];
                        var offset = 0;
                        while (offset < size)
                        {
                            var count = await reader.ReadAsync(data.AsMemory(offset), _stop.Token);
                            if (count == 0) throw new EndOfStreamException();
                            offset += count;
                        }
                        await reader.ReadLineAsync(_stop.Token);
                        Appends++;
                        if (FailAppend) { await Write(tag + " NO synthetic append failure"); continue; }
                        var uid = (uint)(100 + Appends); Messages[uid] = new string(data);
                        if (LoseAppendResponse) return;
                        await Write(tag + $" OK [APPENDUID {Validity} {uid}] appended"); continue;
                    }
                    else if (upper == "LOGOUT") { await Write("* BYE bye"); await Write(tag + " OK logout"); return; }
                    else if (upper is "UNSELECT" or "CLOSE" or "NOOP") { }
                    else throw new InvalidDataException("Unhandled fixture command: " + cmd);
                    await Write(tag + " OK done");
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    static IEnumerable<uint> ParseSet(string set)
    {
        foreach (var part in set.Split(','))
        {
            var ends = part.Split(':'); var first = uint.Parse(ends[0]); var last = uint.Parse(ends[^1]);
            for (var uid = first; uid <= last; uid++) yield return uid;
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _accept;
        await Task.WhenAll(_connections); _stop.Dispose();
    }
}

sealed class FakeSmtp : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly Task _run;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Accepted;
    public string Mime { get; private set; } = "";
    public List<string> Recipients { get; } = new();
    public bool DropOnQuit { get; set; }
    public bool LoseDataResponse { get; set; }
    public TimeSpan DataDelay { get; set; }
    public FakeSmtp() { _listener.Start(); _run = Task.Run(RunAsync); }
    async Task RunAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 synthetic ESMTP");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.StartsWith("EHLO")) await writer.WriteLineAsync("250-localhost\r\n250 AUTH PLAIN");
                else if (line.StartsWith("AUTH")) await writer.WriteLineAsync("235 authenticated");
                else if (line.StartsWith("MAIL FROM")) await writer.WriteLineAsync("250 sender accepted");
                else if (line.StartsWith("RCPT TO")) { Recipients.Add(line); await writer.WriteLineAsync("250 recipient accepted"); }
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 data");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } row && row != ".") data.AppendLine(row.StartsWith("..") ? row[1..] : row);
                    Mime = data.ToString(); Accepted++;
                    if (LoseDataResponse) return;
                    if (DataDelay > TimeSpan.Zero) await Task.Delay(DataDelay, _stop.Token);
                    await writer.WriteLineAsync("250 queued");
                }
                else if (line == "QUIT") { if (!DropOnQuit) await writer.WriteLineAsync("221 bye"); return; }
                else if (line == "RSET") await writer.WriteLineAsync("250 reset");
                else throw new InvalidDataException("Unhandled SMTP fixture command: " + line);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _run; _stop.Dispose(); }
}

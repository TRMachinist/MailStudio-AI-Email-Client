using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;

namespace MailStudio.Regression;
sealed class FakeImageHttp : IAsyncDisposable
{
    readonly TcpListener _listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource _stop=new();readonly Task _run;
    public int Requests;public string Url=>"http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/photo.png";
    public FakeImageHttp(){_listener.Start();_run=RunAsync();}
    async Task RunAsync()
    {
        try{while(!_stop.IsCancellationRequested){using var client=await _listener.AcceptTcpClientAsync(_stop.Token);await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,4096,true);while(await reader.ReadLineAsync(_stop.Token) is {Length:>0}){}Interlocked.Increment(ref Requests);var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aS1sAAAAASUVORK5CYII=");await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"),_stop.Token);await stream.WriteAsync(bytes,_stop.Token);}}
        catch(Exception ex)when(ex is OperationCanceledException or IOException or SocketException){}
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _run;_stop.Dispose();}
}

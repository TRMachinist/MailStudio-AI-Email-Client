using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MailStudio.Services;

/// <summary>One local worker per viewer. No network listener; a closed viewer releases native CAD/OCR memory.</summary>
public sealed class TechnicalWorker : IDisposable
{
    Process? _process;readonly SemaphoreSlim _gate=new(1,1);int _sequence;bool _disposed;
    readonly string? _executable;
    public TechnicalWorker(string? executable=null)=>_executable=executable;
    public static string? DevelopmentPython {get;set;}
    public static string? DevelopmentScript {get;set;}
    void Start()
    {
        if(_process is {HasExited:false})return;
        var exe=_executable??new[]{Path.Combine(AppContext.BaseDirectory,"technical","MailStudio.Technical.exe"),Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","technical","MailStudio.Technical.exe"))}.FirstOrDefault(File.Exists)??"";
        ProcessStartInfo info;
        if(File.Exists(exe))info=new(exe);
        else
        {
            var source=DevelopmentScript??FindSource();var python=DevelopmentPython;
            if(source==null||string.IsNullOrEmpty(python)||!File.Exists(python))throw new FileNotFoundException(L.T("Teknik ek bileşeni bulunamadı. Tam MailStudio paketini kullanın."));
            info=new(python);info.ArgumentList.Add(source);
        }
        info.UseShellExecute=false;info.CreateNoWindow=true;info.RedirectStandardInput=info.RedirectStandardOutput=info.RedirectStandardError=true;info.StandardInputEncoding=info.StandardOutputEncoding=new UTF8Encoding(false);
        _process=Process.Start(info)??throw new IOException(L.T("Teknik ek bileşeni başlatılamadı."));
        _process.ErrorDataReceived+=(_,_)=>{};_process.BeginErrorReadLine();
    }
    static string? FindSource(){var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null){var p=Path.Combine(dir.FullName,"src","MailStudio.Technical","worker.py");if(File.Exists(p))return p;dir=dir.Parent;}return null;}
    public async Task<JsonElement> CallAsync(string operation,object args,CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);Start();var id=++_sequence;
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{id,operation,args}));await _process.StandardInput.FlushAsync(ct);
            using var registration=ct.Register(()=>{try{_process?.Kill(true);}catch{}});
            var line=await _process.StandardOutput.ReadLineAsync(ct)??throw new IOException(L.T("Teknik ek bileşeni beklenmedik şekilde kapandı."));
            if(L.IsEnglish)line=L.TJson(line);
            using var response=JsonDocument.Parse(line);
            if(response.RootElement.GetProperty("id").GetInt32()!=id)throw new IOException(L.T("Teknik ek yanıt sırası geçersiz."));
            if(response.RootElement.TryGetProperty("error",out var error))throw new InvalidDataException(error.GetString());
            return response.RootElement.GetProperty("result").Clone();
        }
        finally{_gate.Release();}
    }
    public void Dispose(){_disposed=true;try{_process?.Kill(true);}catch{} _process?.Dispose();}
}

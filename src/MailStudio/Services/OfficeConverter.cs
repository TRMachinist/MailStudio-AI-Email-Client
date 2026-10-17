using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MailStudio.Services;

/// <summary>
/// Opens Word / Excel / PowerPoint documents read-only and converts them to a PDF view or an editable OpenXML copy.
/// Uses Microsoft Office if it is installed, otherwise the LibreOffice bundled with the app (office folder); so it works on every machine.
/// Runs on a separate STA thread (the UI does not freeze), macros are forcibly disabled, it closes the Office process it started;
/// the user's open PowerPoint is never touched. The result is cached.
/// </summary>
public static class OfficeConverter
{
    public enum App { Word, Excel, PowerPoint }

    public static App? AppFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".rtf" or ".odt" => App.Word,
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".xltx" or ".ods" => App.Excel,
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".odp" => App.PowerPoint,
        _ => null,
    };

    static string ProgId(App a) => a switch { App.Word => "Word.Application", App.Excel => "Excel.Application", _ => "PowerPoint.Application" };
    static string ProcessName(App a) => a switch { App.Word => "WINWORD", App.Excel => "EXCEL", _ => "POWERPNT" };

    public static bool IsInstalled(App a) => Type.GetTypeFromProgID(ProgId(a)) != null;

    /// <summary>If MAILSTUDIO_FORCE_LIBREOFFICE=1 the bundled LibreOffice is used even when Microsoft Office is installed (for tests).</summary>
    static bool ForceLibre => Environment.GetEnvironmentVariable("MAILSTUDIO_FORCE_LIBREOFFICE") == "1";

    static bool UseMsOffice(App a) => !ForceLibre && IsInstalled(a);

    /// <summary>Can this document type be converted for viewing/editing (Microsoft Office or the bundled LibreOffice).</summary>
    public static bool CanConvert(App a) => UseMsOffice(a) || LibreOfficePath != null;

    /// <summary>Name of the converter to use (for the status bar).</summary>
    public static string EngineName(App a) => UseMsOffice(a) ? "Microsoft Office" : "LibreOffice";

    static string? _libre; static bool _libreSearched;
    /// <summary>soffice.com path of the bundled (office\program in the app folder) or system-installed LibreOffice.</summary>
    public static string? LibreOfficePath
    {
        get
        {
            if (_libreSearched) return _libre;
            _libreSearched = true;
            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("MAILSTUDIO_LIBREOFFICE");
            if (!string.IsNullOrWhiteSpace(env)) { candidates.Add(env); candidates.Add(Path.Combine(env, "soffice.com")); candidates.Add(Path.Combine(env, "program", "soffice.com")); }
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "office", "program", "soffice.com"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "office", "program", "soffice.com"));
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "LibreOffice", "program", "soffice.com"));
            foreach (var c in candidates)
            {
                try
                {
                    var p = Path.GetFullPath(c);
                    if (File.Exists(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { var com = Path.ChangeExtension(p, ".com"); if (File.Exists(com)) p = com; }
                    if (File.Exists(p) && (p.EndsWith(".com", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) return _libre = p;
                }
                catch { }
            }
            return null;
        }
    }

    /// <summary>Is it a modern format (docx/xlsx/pptx) the editor can open directly?</summary>
    public static bool IsOpenXml(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm" or ".dotx" or ".xlsx" or ".xlsm" or ".xltx" or ".pptx" or ".pptm" or ".ppsx";

    public static string OpenXmlExtension(App a) => a switch { App.Word => ".docx", App.Excel => ".xlsx", _ => ".pptx" };

    /// <summary>Converts old formats (doc, rtf, odt, xls, ods, xlsb, ppt, odp) into an editable OpenXML copy.</summary>
    public static Task<string> ToOpenXmlAsync(string source, CancellationToken ct)
    {
        var app = AppFor(Path.GetExtension(source)) ?? throw new NotSupportedException(L.T("Bu dosya türü Office ile çevrilemez."));
        var dir = Path.Combine(AppPaths.Root, "edit-copies", Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        return Start(app, source, Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + OpenXmlExtension(app)), true, ct, null);
    }

    public static string CachePath(string source)
    {
        var fi = new FileInfo(source);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..20];
        var dir = Path.Combine(AppPaths.Root, "preview-cache"); Directory.CreateDirectory(dir);
        return Path.Combine(dir, key + ".pdf");
    }

    public static Task<string> ToPdfAsync(string source, CancellationToken ct, TimeSpan? timeout = null)
    {
        var app = AppFor(Path.GetExtension(source)) ?? throw new NotSupportedException(L.T("Bu dosya türü Office ile çevrilemez."));
        var dest = CachePath(source);
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return Task.FromResult(dest);
        return Start(app, source, dest, false, ct, timeout);
    }

    static Task<string> Start(App app, string source, string dest, bool openXml, CancellationToken ct, TimeSpan? timeout)
    {
        if (!UseMsOffice(app))
        {
            if (LibreOfficePath == null) throw new InvalidOperationException(L.T("Belge dönüştürücü (office klasörü) bulunamadı; paket eksik kopyalanmış olabilir."));
            return LibreConvertAsync(app, source, dest, openXml, ct, timeout ?? TimeSpan.FromSeconds(180));
        }
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = Process.GetProcessesByName(ProcessName(app)).Select(p => p.Id).ToHashSet();
        var thread = new Thread(() =>
        {
            try { Run(app, source, dest, before, openXml); tcs.TrySetResult(dest); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = L.T("Office dönüştürme") };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        _ = Task.Delay(limit, ct).ContinueWith(_ =>
        {
            if (tcs.Task.IsCompleted) return;
            // Close the stuck Office process that we started (never the user's).
            foreach (var p in Process.GetProcessesByName(ProcessName(app))) using (p) if (!before.Contains(p.Id) && IsAutomation(p.Id)) try { p.Kill(); } catch { }
            tcs.TrySetException(ct.IsCancellationRequested ? new OperationCanceledException() : new TimeoutException(L.T("Office belgeyi zamanında açamadı.")));
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    static readonly SemaphoreSlim LibreGate = new(1, 1);

    /// <summary>
    /// Headless (windowless) conversion with the bundled LibreOffice. Uses its own profile folder (never touches the user's LibreOffice),
    /// conversions run one at a time, the source file is copied to a temp folder (the original never changes or gets locked), on timeout only
    /// the process tree it started is closed. Macros do not run under LibreOffice's default high security setting.
    /// </summary>
    static async Task<string> LibreConvertAsync(App app, string source, string dest, bool openXml, CancellationToken ct, TimeSpan timeout)
    {
        var filter = openXml
            ? app switch { App.Word => "docx:MS Word 2007 XML", App.Excel => "xlsx:Calc MS Excel 2007 XML", _ => "pptx:Impress MS PowerPoint 2007 XML" }
            : app switch { App.Word => "pdf:writer_pdf_Export", App.Excel => "pdf:calc_pdf_Export", _ => "pdf:impress_pdf_Export" };
        var work = Path.Combine(Path.GetTempPath(), "MailStudio-lo-" + Guid.NewGuid().ToString("N")[..10]);
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(outDir);
        await LibreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var input = Path.Combine(work, "belge" + Path.GetExtension(source).ToLowerInvariant());
            File.Copy(source, input, true);
            var profile = Path.Combine(AppPaths.Root, "office-profile");
            Directory.CreateDirectory(profile);
            var psi = new ProcessStartInfo(LibreOfficePath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(LibreOfficePath!)!,
            };
            foreach (var a in new[] { "--headless", "--invisible", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
                                      "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri,
                                      "--convert-to", filter, "--outdir", outDir, input })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new IOException(L.T("LibreOffice başlatılamadı."));
            var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            using var lim = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lim.CancelAfter(timeout);
            try { await p.WaitForExitAsync(lim.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException(L.T("Belge zamanında dönüştürülemedi."));
            }
            var produced = Directory.GetFiles(outDir).FirstOrDefault(f => new FileInfo(f).Length > 0);
            if (produced == null)
            {
                var msg = ((await se.ConfigureAwait(false)) + " " + (await so.ConfigureAwait(false))).Trim();
                throw new IOException(L.T("Belge dönüştürülemedi.") + (msg.Length > 0 ? " (" + (msg.Length > 300 ? msg[..300] : msg) + ")" : ""));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(produced, dest, true);
            return dest;
        }
        finally
        {
            LibreGate.Release();
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>Only Office processes started with /automation or -Embedding (not opened by the user).</summary>
    static bool IsAutomation(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
            foreach (var o in searcher.Get()) { var cmd = o["CommandLine"] as string ?? ""; return cmd.Contains("/automation", StringComparison.OrdinalIgnoreCase) || cmd.Contains("-Embedding", StringComparison.OrdinalIgnoreCase); }
        }
        catch { }
        return false;
    }

    static void Run(App app, string source, string dest, HashSet<int> before, bool openXml)
    {
        var tmp = dest + (openXml ? ".tmp" + Path.GetExtension(dest) : ".tmp.pdf");
        dynamic? office = null; dynamic? doc = null; bool ours = true;
        try
        {
            var type = Type.GetTypeFromProgID(ProgId(app))!;
            if (app == App.PowerPoint) ours = !before.Any();   // PowerPoint runs as a single instance; if the user keeps it open it is not closed
            office = Activator.CreateInstance(type)!;
            try { office.AutomationSecurity = 3; } catch { }     // msoAutomationSecurityForceDisable: macros do not run
            switch (app)
            {
                case App.Word:
                    office.Visible = false; office.DisplayAlerts = 0;
                    doc = office.Documents.Open(source, false, true, false); // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles
                    if (openXml) doc.SaveAs2(tmp, 16); else doc.ExportAsFixedFormat(tmp, 17);   // wdFormatDocumentDefault / wdExportFormatPDF
                    doc.Close(0); doc = null;
                    break;
                case App.Excel:
                    office.Visible = false; office.DisplayAlerts = false; office.ScreenUpdating = false;
                    doc = office.Workbooks.Open(source, 0, true);              // UpdateLinks=0, ReadOnly
                    if (openXml) doc.SaveAs(tmp, 51); else doc.ExportAsFixedFormat(0, tmp);    // xlOpenXMLWorkbook / xlTypePDF
                    doc.Close(false); doc = null;
                    break;
                default:
                    doc = office.Presentations.Open(source, -1, 0, 0);        // ReadOnly, Untitled=false, WithWindow=false
                    doc.SaveAs(tmp, openXml ? 24 : 32);                        // ppSaveAsOpenXMLPresentation / ppSaveAsPDF
                    doc.Close(); doc = null;
                    break;
            }
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) throw new IOException(L.T("Office çıktı üretmedi."));
            File.Move(tmp, dest, true);
        }
        finally
        {
            try { if (doc != null) doc.Close(false); } catch { }
            if (office != null)
            {
                try { if (ours) office.Quit(); } catch { }
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(office); } catch { }
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            doc = null; office = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (ours)
            {
                // If the Office process started for automation does not close (a lingering COM reference) it is closed after a short wait.
                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < deadline)
                {
                    var left = Process.GetProcessesByName(ProcessName(app)).Where(p => !before.Contains(p.Id)).ToList();
                    if (left.Count == 0) break;
                    foreach (var p in left) p.Dispose();
                    Thread.Sleep(300);
                }
                foreach (var p in Process.GetProcessesByName(ProcessName(app)))
                    using (p) if (!before.Contains(p.Id)) try { if (IsAutomation(p.Id)) p.Kill(); } catch { }
            }
        }
    }
}

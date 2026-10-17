using System.Diagnostics;
using System.IO;
using System.Windows;
using MailStudio.Services;
using MailStudio.Services.Ai;

namespace MailStudio.Views;

/// <summary>Chooses a suitable viewer by attachment and file type. Unknown types open with the default program in Windows.</summary>
public static class FileViewers
{
    public static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

    public static Window? Create(string path, ComposeAiOptions ai)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return new PdfAttachmentWindow(path, ai);
        if (ext is ".step" or ".stp") return new StepAttachmentWindow(path);
        if (ext is ".dxf" or ".dwg") return new CadDrawingWindow(path);
        if (ext is ".dwf" or ".dwfx" or ".xps" or ".oxps") return new XpsViewerWindow(path);
        if (Images.Contains(ext)) return new ImageAttachmentWindow(path);
        return DocumentViewerWindow.Classify(path) is { } kind ? new DocumentViewerWindow(path, kind) : null;
    }

    /// <summary>Opens the viewer; if there is no suitable viewer it hands over to the default program.</summary>
    /// <summary>Executable / script types: never run directly from an attachment.</summary>
    public static readonly string[] Executable = { ".exe", ".msi", ".msix", ".appx", ".com", ".scr", ".pif", ".cpl", ".jar", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".reg", ".iso", ".img", ".vhd", ".vhdx", ".dll", ".sys", ".msc", ".application", ".appref-ms" };

    public static void Open(string path, Window? owner, ComposeAiOptions ai)
    {
        var viewer = Create(path, ai);
        if (viewer == null)
        {
            if (Executable.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                var answer = MessageBox.Show(owner ?? Application.Current?.MainWindow!, L.F("{0} çalıştırılabilir bir dosya. Güvenlik için MailStudio bu türü doğrudan açmaz.\n\nDosyayı klasörde göstermek ister misiniz?", Path.GetFileName(path)), L.T("MailStudio — güvenlik"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return;
        }
        if (owner != null && owner.IsVisible) viewer.Owner = owner;
        ReturnFocusOnClose(viewer);
        viewer.Show();
    }

    /// <summary>
    /// When an owned window closes Windows may activate another app in the z-order and MailStudio falls behind.
    /// The owner is activated right before closing. Skipped if the window hands over to another viewer (Office → PDF).
    /// </summary>
    public static void ReturnFocusOnClose(Window w)
    {
        w.Closing += (_, e) =>
        {
            if (e.Cancel || HandingOff.Contains(w)) return;
            if (w.Owner is { IsVisible: true } o) { if (o.WindowState == WindowState.Minimized) o.WindowState = WindowState.Normal; o.Activate(); }
        };
        w.Closed += (_, _) => HandingOff.Remove(w);
    }
    public static readonly HashSet<Window> HandingOff = new();

    /// <summary>Opens the Word / Excel / PowerPoint editor. Old formats (doc, xls, ppt, odt…) are first converted to an OpenXML copy.</summary>
    public static async Task<Window?> OpenEditorAsync(string source, Window? owner, Action<string>? status = null)
    {
        var app = OfficeConverter.AppFor(Path.GetExtension(source));
        if (app == null) return null;
        var path = source;
        if (!OfficeConverter.IsOpenXml(source))
        {
            if (!OfficeConverter.CanConvert(app.Value)) { status?.Invoke(L.T("Belge dönüştürücü bulunamadı (uygulama klasöründeki office klasörü eksik).")); return null; }
            status?.Invoke(L.T("Düzenleme için modern biçime çevriliyor…"));
            path = await OfficeConverter.ToOpenXmlAsync(source, CancellationToken.None);
        }
        Window w = app.Value switch
        {
            OfficeConverter.App.Excel => new Editors.SpreadsheetEditorWindow(path, source),
            OfficeConverter.App.Word => new Editors.WordEditorWindow(path, source),
            _ => new Editors.SlideEditorWindow(path, source),
        };
        var realOwner = owner?.Owner ?? (owner is { IsVisible: true } && owner is not PdfAttachmentWindow ? owner : Application.Current?.MainWindow is { IsVisible: true } m && m != owner ? m : null);
        if (realOwner != null) w.Owner = realOwner;
        ReturnFocusOnClose(w);
        w.Show(); w.Activate();
        return w;
    }

    public static ComposeAiOptions DefaultAi()
    {
        var s = AppSettings.Shared;
        var list = new List<IAiProvider> { new OllamaProvider(() => s.OllamaUrl), new ClaudeCliProvider(), new CodexCliProvider(), new GeminiCliProvider() };
        return new ComposeAiOptions(list, list[0].Name, null);
    }
}

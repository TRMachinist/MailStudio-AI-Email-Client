using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailStudio.Services;

/// <summary>
/// Prints a self-contained HTML document to PDF with WebView2 (A4, backgrounds on, no header/footer).
/// The WebView lives in an invisible off-screen window; scripts are disabled and nothing is loaded from the network.
/// </summary>
public static class HtmlPdfPrinter
{
    public static async Task PrintAsync(string html, string pdfPath, CancellationToken ct = default)
    {
        var host = new Window
        {
            Width = 900, Height = 1200, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, Title = "MailStudio PDF"
        };
        var web = new WebView2();
        host.Content = web;
        host.Show();
        try
        {
            await web.EnsureCoreWebView2Async(await MailHtml.EnvAsync());
            var core = web.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            // block every request (the document carries its images as data: URIs)
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !e.Request.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.NavigateToString(html);
            using (ct.Register(() => loaded.TrySetCanceled()))
                if (!await loaded.Task) throw new InvalidOperationException(L.T("Belge yüklenemedi."));

            var settings = core.Environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.PageWidth = 8.27; settings.PageHeight = 11.69;              // A4 in inches
            settings.MarginTop = settings.MarginBottom = 0.4; settings.MarginLeft = settings.MarginRight = 0.4;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pdfPath))!);
            var tmp = pdfPath + ".tmp";
            if (!await core.PrintToPdfAsync(tmp, settings)) throw new InvalidOperationException(L.T("PDF yazılamadı."));
            File.Move(tmp, pdfPath, true);
        }
        finally
        {
            web.Dispose();
            host.Close();
        }
    }
}

using System.Text;
using System.Windows;
using MailStudio.Models;
using MailStudio.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailStudio;

/// <summary>Shared HTML/WebView2 settings for the reading pane and the message window.</summary>
public static class MailHtml
{
    /// <summary>Theme CSS: in the dark theme only the page background and the default text color change; the mail's own colors are kept.</summary>
    public static string BaseCss => Services.ThemeManager.IsDark
        ? "<style>:root{color-scheme:dark}html{background:#1E293B}" +
          "body{font-family:Calibri,'Segoe UI',sans-serif;font-size:14.5px;color:#E2E8F0;margin:12px 14px;word-wrap:break-word;overflow-wrap:anywhere}" +
          "a{color:#7DD3FC}img{max-width:100%;height:auto}table{max-width:100%}</style>"
        : "<style>:root{color-scheme:light}html{background:#fff}" +
          "body{font-family:Calibri,'Segoe UI',sans-serif;font-size:14.5px;color:#222;margin:12px 14px;word-wrap:break-word;overflow-wrap:anywhere}" +
          "img{max-width:100%;height:auto}table{max-width:100%}</style>";

    static CoreWebView2Environment? _env;

    public static async Task<CoreWebView2Environment> EnvAsync() =>
        _env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Root, "webview"));

    public static string Wrap(string html)
    {
        html=System.Text.RegularExpressions.Regex.Replace(html,@"(\bsrc\s*=\s*['""])(//)","$1https://",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // The text is already decoded to Unicode; if the mail's own charset label (e.g. windows-1254) stays,
        // the browser re-interprets it and breaks Turkish characters ("payla°lan").
        html = System.Text.RegularExpressions.Regex.Replace(html, "<meta[^>]*charset[^>]*>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase))
            html = "<html><head><meta charset=\"utf-8\"></head><body>" + html + "</body></html>";
        if (html.Contains("<head>", StringComparison.OrdinalIgnoreCase))
            html = html.Replace("<head>", "<head><meta charset=\"utf-8\">" + BaseCss, StringComparison.OrdinalIgnoreCase);
        else html = BaseCss + html;
        return html;
    }

    public static void Show(WebView2 web, string html)
    {
        html = Wrap(html);
        if (Encoding.UTF8.GetByteCount(html) < 1_500_000) web.NavigateToString(html);
        else
        {
            var tmp = Path.Combine(AppPaths.Root, $"view-{Guid.NewGuid():N}.html");
            File.WriteAllText(tmp, html, Encoding.UTF8);
            web.CoreWebView2.Navigate(new Uri(tmp).AbsoluteUri);
        }
    }
}

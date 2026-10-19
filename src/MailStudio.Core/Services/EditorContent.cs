using Ganss.Xss;

namespace MailStudio.Services;

/// <summary>Shared allowlist for mail quotes, signatures, clipboard HTML and imported drafts.</summary>
public static class EditorContent
{
    public static string Sanitize(string? html)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(new[] { "http", "https", "mailto", "data" });
        sanitizer.AllowedAttributes.Add("class");
        // Editor control IDs and message-supplied nonces must never enter the application DOM.
        sanitizer.AllowedAttributes.Remove("id");
        sanitizer.AllowedAttributes.Remove("contenteditable");
        sanitizer.AllowedAttributes.Remove("nonce");
        sanitizer.AllowedTags.ExceptWith(new[] { "form", "input", "button", "textarea", "select", "option", "keygen" });
        html ??= "";
        // Outlook/Exchange write "data:IMAGE/PNG;base64,..." - the sanitizer drops upper-case data URIs (empty boxes in quotes)
        html = System.Text.RegularExpressions.Regex.Replace(html, @"data:(image/[a-z0-9.+-]+)(?=;base64,)", m => "data:" + m.Groups[1].Value.ToLowerInvariant(), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        // Mail-client <style> blocks (p.MsoNormal{margin:0}, span.x{font-family:Wingdings}) are removed by sanitizing; keep their effect inline
        if (html.Contains("<style", StringComparison.OrdinalIgnoreCase))
        {
            var styled = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
            if (SignatureImport.InlineStyles(styled)) html = styled.Body?.InnerHtml ?? html;
        }
        var clean = sanitizer.Sanitize(html);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(clean);
        foreach (var element in document.QuerySelectorAll("[href]"))
            if (element.GetAttribute("href")?.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
                element.RemoveAttribute("href");
        foreach (var element in document.QuerySelectorAll("[src]"))
        {
            var src = element.GetAttribute("src") ?? "";
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                !System.Text.RegularExpressions.Regex.IsMatch(src, @"^data:image/(png|jpeg|gif|bmp|webp);base64,", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                element.RemoveAttribute("src");
        }
        return document.Body?.InnerHtml ?? "";
    }
}

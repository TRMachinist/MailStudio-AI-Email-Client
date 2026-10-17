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
        var clean = sanitizer.Sanitize(html ?? "");
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(clean);
        foreach (var element in document.QuerySelectorAll("[href]"))
            if (element.GetAttribute("href")?.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
                element.RemoveAttribute("href");
        foreach (var element in document.QuerySelectorAll("[src]"))
        {
            var src = element.GetAttribute("src") ?? "";
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                !System.Text.RegularExpressions.Regex.IsMatch(src, @"^data:image/(png|jpeg|gif|bmp|webp);base64,", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                element.RemoveAttribute("src");
        }
        return document.Body?.InnerHtml ?? "";
    }
}

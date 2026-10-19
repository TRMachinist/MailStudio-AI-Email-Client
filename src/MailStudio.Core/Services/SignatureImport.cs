using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MailStudio.Services;

/// <summary>
/// Turns ready-made HTML signatures (pasted code, .htm files, Outlook's Signatures folder) into a self-contained,
/// sanitized signature: simple &lt;style&gt; rules are inlined, local and (optionally) remote images become data URIs,
/// so the logo travels with the mail as an inline attachment.
/// </summary>
public static class SignatureImport
{
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Outlook signature folders (the folder name is localized: Signatures, İmzalar, ...).</summary>
    public static IEnumerable<string> OutlookFolders()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft");
        foreach (var name in new[] { "Signatures", "İmzalar", "Imzalar", "Signaturen", "Signatures (Outlook)" })
        {
            var p = Path.Combine(root, name);
            if (Directory.Exists(p)) yield return p;
        }
    }

    /// <summary>Outlook signatures found on this computer: (name, .htm path).</summary>
    public static List<(string Name, string Path)> FindOutlookSignatures(IEnumerable<string>? folders = null)
    {
        var list = new List<(string, string)>();
        foreach (var dir in folders ?? OutlookFolders())
            foreach (var f in Directory.EnumerateFiles(dir, "*.htm").Concat(Directory.EnumerateFiles(dir, "*.html")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                list.Add((Path.GetFileNameWithoutExtension(f), f));
        return list;
    }

    /// <summary>Reads an .htm file honoring its BOM or &lt;meta charset&gt; (Outlook often writes windows-1254).</summary>
    public static string ReadHtmlFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = Regex.Match(head, @"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(m.Groups[1].Value).GetString(bytes); }
            catch (ArgumentException) { }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Prepares an HTML signature. <paramref name="baseDir"/> resolves relative image paths (only inside that
    /// folder); <paramref name="fetch"/> downloads http(s) images, or null to keep them as links.</summary>
    public static async Task<string> PrepareAsync(string? html, string? baseDir = null, Func<string, Task<byte[]?>>? fetch = null)
    {
        var doc = new HtmlParser().ParseDocument(html ?? "");
        InlineStyles(doc);
        foreach (var img in doc.QuerySelectorAll("img[src]").ToList())
        {
            var src = (img.GetAttribute("src") ?? "").Trim();
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) continue;
            byte[]? data = null; string? name = src;
            if (Regex.IsMatch(src, "^https?://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (fetch != null) { try { data = await fetch(src); } catch { data = null; } }
                if (data == null) continue; // keep the link
            }
            else if (baseDir != null)
            {
                var local = LocalPath(src, baseDir);
                if (local != null && File.Exists(local) && new FileInfo(local).Length <= MaxImageBytes) { data = File.ReadAllBytes(local); name = local; }
            }
            var mime = data == null ? null : ImageMime(data, name);
            if (data == null || mime == null || data.Length > MaxImageBytes) { img.Remove(); continue; }
            img.SetAttribute("src", $"data:{mime};base64,{Convert.ToBase64String(data)}");
        }
        var body = doc.Body?.InnerHtml ?? "";
        var clean = EditorContent.Sanitize(body).Trim();
        // Outlook ends signatures with empty paragraphs (<p>&nbsp;</p>): drop trailing blanks
        clean = Regex.Replace(clean, @"(\s*<p[^>]*>(\s|&nbsp;|<o:p>|</o:p>|<span[^>]*>|</span>|<br>)*</p>)+\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return clean;
    }

    static string? LocalPath(string src, string baseDir)
    {
        try
        {
            string path;
            if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) path = new Uri(src).LocalPath;
            else path = Path.Combine(baseDir, Uri.UnescapeDataString(src).Replace('/', Path.DirectorySeparatorChar));
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null; // never read outside the signature folder
        }
        catch { return null; }
    }

    public static string? ImageMime(byte[] d, string? name = null)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8) return "image/jpeg";
        if (d.Length > 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return "image/gif";
        if (d.Length > 2 && d[0] == 'B' && d[1] == 'M') return "image/bmp";
        if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "image/webp";
        return null;
    }

    /// <summary>Copies simple rules (tag, .class, tag.class, comma lists) from &lt;style&gt; into style attributes;
    /// existing inline styles win. Sanitizing would otherwise drop the &lt;style&gt; block.</summary>
    public static bool InlineStyles(IDocument doc)
    {
        var css = string.Join("\n", doc.QuerySelectorAll("style").Select(s => s.TextContent));
        if (css.Length == 0) return false;
        css = Regex.Replace(css, @"/\*.*?\*/|<!--|-->", "", RegexOptions.Singleline);
        css = Regex.Replace(css, @"@[\w-]+[^{;]*\{([^{}]*\{[^{}]*\})*[^{}]*\}|@[^;{]+;", "", RegexOptions.Singleline); // @page, @font-face, @media
        var rules = new List<(string Selector, string Decl)>();
        foreach (Match m in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var decl = Regex.Replace(m.Groups[2].Value, @"\s*mso-[\w-]+\s*:[^;]*;?", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim().Trim(';').Trim();
            if (decl.Length == 0) continue;
            foreach (var sel in m.Groups[1].Value.Split(','))
            {
                var s = sel.Trim();
                if (Regex.IsMatch(s, @"^[a-zA-Z][\w]*$|^[a-zA-Z]*\.[\w-]+$")) rules.Add((s, decl));
            }
        }
        var changed = false;
        foreach (var el in doc.Body?.QuerySelectorAll("*").ToList() ?? new())
        {
            var add = new StringBuilder();
            foreach (var (sel, decl) in rules)
            {
                var dot = sel.IndexOf('.');
                var tag = dot < 0 ? sel : sel[..dot];
                var cls = dot < 0 ? null : sel[(dot + 1)..];
                if (tag.Length > 0 && !el.LocalName.Equals(tag, StringComparison.OrdinalIgnoreCase)) continue;
                if (cls != null && !el.ClassList.Contains(cls)) continue;
                add.Append(decl).Append(';');
            }
            if (add.Length == 0) continue;
            var own = el.GetAttribute("style");
            el.SetAttribute("style", add + (string.IsNullOrWhiteSpace(own) ? "" : own));
            changed = true;
        }
        return changed;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MailStudio.Services;

/// <summary>
/// Detects missing attachment references in composed emails (e.g. "ekte", "attached", "enclosed")
/// while ignoring false alarms, signatures, and quoted replies.
/// </summary>
public static class AttachmentReminder
{
    // False alarm phrases that contain trigger words but do not mean an email attachment.
    static readonly string[] FalseAlarmPhrases =
    [
        "ek olarak",
        "ek bilgi",
        "ek bir",
        "ek süre",
        "ek ücret",
        "ek ücreti",
        "ek ders",
        "ek bina",
        "ek sefer",
        "ek seferler",
        "ek bütçe",
        "ek bütçesi",
        "ek kontenjan",
        "ek madde",
        "ek protokol",
        "ek ödeme",
        "ek gösterge",
        "ek iş",
        "ek işler",
        "no strings attached",
        "emotional attachment",
        "emotionally attached",
        "sentimentally attached",
        "attached to the project",
        "attached to the company",
        "attached to the team",
        "attached to the idea",
        "attached to the hip"
    ];

    // Regular expressions for detecting attachment hints in Turkish and English
    static readonly Regex[] TriggerPatterns =
    [
        // Turkish triggers
        new(@"\bekte\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bekli\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bekteki\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bektedir\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bektedirler\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bekledim\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bekliyorum\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bekliyoruz\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bilişikte\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bilişikteki\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bilişiktedir\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bek\s+dosya(da|lar|ları)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bdosya(lar)?\s+ekte\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // English triggers
        new(@"\battached\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\battachment(s)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\benclosed\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\benclosing\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bsee\s+attach(ed)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bfind\s+(the\s+)?attach(ed)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bherewith\s+attached\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    static readonly Regex UrlRegex = new(@"https?://\S+|www\.\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex EmailRegex = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);

    /// <summary>
    /// Strips quotes, headers, and signatures from plain text or HTML to isolate the user's active message.
    /// </summary>
    public static string StripQuotesAndSignatures(string? html, string? text, string? draftText = null, string? quotedHtml = null, string? signatureHtml = null)
    {
        // 1. If explicit draft text is provided, use it directly
        if (!string.IsNullOrWhiteSpace(draftText))
            return draftText.Trim();

        // 2. If HTML is provided, strip quote & signature sections
        if (!string.IsNullOrWhiteSpace(html))
        {
            var cleanHtml = html;
            // Remove ms-quote and ms-signature containers
            cleanHtml = Regex.Replace(cleanHtml, @"<div\s+id=""ms-quote""[^>]*>[\s\S]*?</div>(\s*</div>)?", "", RegexOptions.IgnoreCase);
            cleanHtml = Regex.Replace(cleanHtml, @"<div\s+id=""ms-signature""[^>]*>[\s\S]*?</div>(\s*</div>)?", "", RegexOptions.IgnoreCase);
            cleanHtml = Regex.Replace(cleanHtml, @"<div\s+class=""ms-quote-section""[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
            cleanHtml = Regex.Replace(cleanHtml, @"<div\s+class=""ms-signature-section""[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
            cleanHtml = Regex.Replace(cleanHtml, @"<div\s+class=""gmail_quote""[^>]*>[\s\S]*?</div>", "", RegexOptions.IgnoreCase);
            cleanHtml = Regex.Replace(cleanHtml, @"<blockquote[^>]*>[\s\S]*?</blockquote>", "", RegexOptions.IgnoreCase);

            var plain = HtmlToPlainText(cleanHtml);
            if (!string.IsNullOrWhiteSpace(plain))
                return StripPlainTextQuotes(plain);
        }

        // 3. Fallback to plain text stripping
        return StripPlainTextQuotes(text ?? "");
    }

    /// <summary>
    /// Strips quoted replies, forwarded headers, and common email signatures from plain text.
    /// </summary>
    public static string StripPlainTextQuotes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.Trim();

            // Check quote boundaries
            if (trimmed.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("----- Özgün İleti -----", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("----- Orijinal Mesaj -----", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("________________________________", StringComparison.OrdinalIgnoreCase))
                break;

            if (Regex.IsMatch(trimmed, @"^On\s+.+?\s+wrote:\s*$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^.+?\s+tarihinde\s+.+?\s+yazdı:\s*$", RegexOptions.IgnoreCase))
                break;

            if (Regex.IsMatch(trimmed, @"^(From|Kimden):\s*.+", RegexOptions.IgnoreCase))
            {
                // Verify if subsequent lines look like header lines
                var isHeaderBlock = false;
                for (var j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                {
                    var next = lines[j].Trim();
                    if (Regex.IsMatch(next, @"^(Sent|Date|To|Subject|Gönderildi|Tarih|Kime|Konu):", RegexOptions.IgnoreCase))
                    {
                        isHeaderBlock = true;
                        break;
                    }
                }
                if (isHeaderBlock) break;
            }

            // Ignore quoted lines
            if (trimmed.StartsWith('>') || trimmed.StartsWith('|')) continue;

            // Check signature delimiters
            if (trimmed == "--" || trimmed == "-- ") break;

            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

    static string HtmlToPlainText(string html)
    {
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|div|tr|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return text;
    }

    /// <summary>
    /// Analyzes the text and determines if an attachment is mentioned.
    /// Returns true if a keyword is found, along with the matched keyword.
    /// </summary>
    public static bool HasAttachmentHint(string? text, out string? matchedKeyword)
    {
        matchedKeyword = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Clean URLs and email addresses so e.g. "https://example.com/attached_files" doesn't trigger
        var cleaned = UrlRegex.Replace(text, " ");
        cleaned = EmailRegex.Replace(cleaned, " ");

        // Normalize text for Turkish-aware comparison
        // Replace Turkish capital 'İ' and 'I' before ToLowerInvariant
        var normalized = cleaned.Replace('İ', 'i').Replace('I', 'ı').ToLowerInvariant();

        // Mask out false alarm phrases
        foreach (var phrase in FalseAlarmPhrases)
        {
            var pNorm = phrase.Replace('İ', 'i').Replace('I', 'ı').ToLowerInvariant();
            normalized = normalized.Replace(pNorm, " ");
        }

        // Test each trigger pattern
        foreach (var pattern in TriggerPatterns)
        {
            var match = pattern.Match(normalized);
            if (match.Success)
            {
                matchedKeyword = match.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a warning should be displayed when sending an email.
    /// Returns true if attachment count is zero and active message mentions an attachment.
    /// </summary>
    public static bool ShouldWarn(
        string? bodyHtml,
        string? bodyText,
        int attachmentCount,
        out string? matchedKeyword,
        string? draftText = null,
        string? quotedHtml = null,
        string? signatureHtml = null)
    {
        matchedKeyword = null;
        if (attachmentCount > 0) return false;

        var message = StripQuotesAndSignatures(bodyHtml, bodyText, draftText, quotedHtml, signatureHtml);
        return HasAttachmentHint(message, out matchedKeyword);
    }

    public static bool ShouldWarn(
        string? bodyHtml,
        string? bodyText,
        int attachmentCount,
        string? draftText,
        string? quotedHtml,
        string? signatureHtml,
        out string? matchedKeyword)
    {
        return ShouldWarn(bodyHtml, bodyText, attachmentCount, out matchedKeyword, draftText, quotedHtml, signatureHtml);
    }
}

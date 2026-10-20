using System.Text.RegularExpressions;
using MailStudio.Services;

namespace MailStudio.Models;

/// <summary>
/// Email response template model.
/// Supports variable replacement for manufacturing and sales workflows.
/// </summary>
public sealed class EmailTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Category { get; set; } = "Genel";
    public string Language { get; set; } = "all"; // "tr", "en", "all"
    public bool IsBuiltIn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public EmailTemplate Clone() => new()
    {
        Id = Id,
        Title = Title,
        Subject = Subject,
        Body = Body,
        Category = Category,
        Language = Language,
        IsBuiltIn = IsBuiltIn,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

/// <summary>
/// Context data extracted from recipient, mail, and company for variable interpolation.
/// </summary>
public sealed class TemplateContext
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FullName { get; set; }
    public string? Company { get; set; }
    public string? Subject { get; set; }
    public string? PartNumber { get; set; }
    public string? Date { get; set; }
    public string? Signature { get; set; }

    public static TemplateContext Create(string? to = null, string? subject = null, string? signature = null, string? bodyForPartExtraction = null, DateTime? date = null)
    {
        var ctx = new TemplateContext
        {
            Date = (date ?? DateTime.Today).ToString("dd.MM.yyyy"),
            Signature = signature ?? ""
        };

        if (!string.IsNullOrWhiteSpace(to))
        {
            var match = Regex.Match(to, @"^""?([^""<]+)""?\s*<([^>]+)>");
            string namePart = "";
            string emailPart = "";
            if (match.Success)
            {
                namePart = match.Groups[1].Value.Trim();
                emailPart = match.Groups[2].Value.Trim();
            }
            else
            {
                var atIdx = to.IndexOf('@');
                if (atIdx > 0)
                {
                    emailPart = to.Trim();
                    namePart = to[..atIdx].Replace('.', ' ').Replace('_', ' ').Trim();
                }
                else
                {
                    namePart = to.Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(namePart))
            {
                ctx.FullName = namePart;
                var tokens = namePart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 1)
                {
                    ctx.FirstName = tokens[0];
                }
                else if (tokens.Length > 1)
                {
                    ctx.FirstName = tokens[0];
                    ctx.LastName = string.Join(" ", tokens.Skip(1));
                }
            }

            if (!string.IsNullOrWhiteSpace(emailPart) && emailPart.Contains('@'))
            {
                var domain = emailPart.Split('@')[^1].ToLowerInvariant();
                var dot = domain.IndexOf('.');
                if (dot > 0)
                {
                    var corp = domain[..dot];
                    if (corp is not ("gmail" or "hotmail" or "outlook" or "yahoo" or "yandex" or "icloud"))
                    {
                        ctx.Company = char.ToUpperInvariant(corp[0]) + corp[1..];
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(subject))
        {
            var clean = Regex.Replace(
                subject,
                @"^\s*((re|fw|fwd|ynt|ilt|ileti|yanıt)\s*:\s*)+",
                "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            ).Trim();
            ctx.Subject = clean;

            var partMatch = Regex.Match(clean, @"\b([A-Za-z0-9]{2,8}[-_][A-Za-z0-9]{2,10})\b");
            if (partMatch.Success)
            {
                ctx.PartNumber = partMatch.Groups[1].Value;
            }
            else
            {
                var (part, rev) = RevisionService.ParsePartAndRevision(clean);
                if (!string.IsNullOrWhiteSpace(rev) && !string.IsNullOrWhiteSpace(part))
                    ctx.PartNumber = part;
            }
        }

        if (string.IsNullOrWhiteSpace(ctx.PartNumber) && !string.IsNullOrWhiteSpace(bodyForPartExtraction))
        {
            var partMatch = Regex.Match(bodyForPartExtraction, @"\b([A-Za-z0-9]{2,8}[-_][A-Za-z0-9]{2,10})\b");
            if (partMatch.Success)
            {
                ctx.PartNumber = partMatch.Groups[1].Value;
            }
            else
            {
                var (part, rev) = RevisionService.ParsePartAndRevision(bodyForPartExtraction);
                if (!string.IsNullOrWhiteSpace(rev) && !string.IsNullOrWhiteSpace(part))
                    ctx.PartNumber = part;
            }
        }

        return ctx;
    }
}

/// <summary>
/// Renders templates with variable substitution:
/// {Ad}, {Soyad}, {Firma}, {Konu}, {ParçaNo}, {Tarih}, {İmza}
/// and English equivalents {FirstName}, {LastName}, {Company}, {Subject}, {PartNumber}, {Date}, {Signature}.
/// </summary>
public static class TemplateEngine
{
    public static string Render(string templateText, TemplateContext context)
    {
        if (string.IsNullOrEmpty(templateText)) return "";

        var firstName = context.FirstName ?? context.FullName ?? "";
        var lastName = context.LastName ?? "";
        var company = context.Company ?? "";
        var subject = context.Subject ?? "";
        var partNo = context.PartNumber ?? "";
        var date = context.Date ?? DateTime.Today.ToString("dd.MM.yyyy");
        var signature = context.Signature ?? "";

        var result = templateText;
        result = ReplaceVar(result, @"\{(?i:Ad|FirstName)\}", firstName);
        result = ReplaceVar(result, @"\{(?i:Soyad|LastName)\}", lastName);
        result = ReplaceVar(result, @"\{(?i:Firma|Company)\}", company);
        result = ReplaceVar(result, @"\{(?i:Konu|Subject)\}", subject);
        result = ReplaceVar(result, @"\{(?i:ParçaNo|ParcaNo|PartNumber|PartNo)\}", partNo);
        result = ReplaceVar(result, @"\{(?i:Tarih|Date)\}", date);
        result = ReplaceVar(result, @"\{(?i:İmza|Imza|Signature)\}", signature);

        return result;
    }

    static string ReplaceVar(string input, string pattern, string replacement)
    {
        // MatchEvaluator: "$" in user data (a subject like "Fiyat $0") is inserted literally, not as a group reference
        return Regex.Replace(input, pattern, _ => replacement, RegexOptions.CultureInvariant);
    }

    public static string TextToHtml(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "<div><br></div>";
        var lines = plainText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                sb.Append("<div><br></div>");
            }
            else
            {
                sb.Append("<div>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</div>");
            }
        }
        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// Parsed information from List-Unsubscribe and List-Unsubscribe-Post headers (RFC 2369, RFC 8058).
/// </summary>
public sealed class UnsubscribeInfo
{
    public string? MailtoUri { get; set; }
    public string? HttpUri { get; set; }
    public bool HasOneClick { get; set; }
    public string RawHeader { get; set; } = "";

    public bool HasAction => !string.IsNullOrEmpty(HttpUri) || !string.IsNullOrEmpty(MailtoUri);
}

/// <summary>
/// Helper utilities for newsletters, List-Unsubscribe header parsing, and rule automation.
/// </summary>
public static class NewsletterHelper
{
    static readonly Regex UriRegex = new(@"<([^>]+)>|([^\s,]+)", RegexOptions.Compiled);

    /// <summary>
    /// Parses RFC 2369 / RFC 8058 List-Unsubscribe and List-Unsubscribe-Post headers.
    /// Handles multiple comma-separated URIs wrapped in angle brackets.
    /// </summary>
    public static UnsubscribeInfo? ParseUnsubscribe(string? listUnsubscribe, string? listUnsubscribePost = null)
    {
        if (string.IsNullOrWhiteSpace(listUnsubscribe)) return null;

        var info = new UnsubscribeInfo { RawHeader = listUnsubscribe.Trim() };
        var matches = UriRegex.Matches(listUnsubscribe);

        foreach (Match m in matches)
        {
            var val = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (string.IsNullOrEmpty(val)) continue;

            if (val.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                info.MailtoUri ??= val;
            }
            else if (val.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                     val.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                info.HttpUri ??= val;
            }
        }

        if (!string.IsNullOrWhiteSpace(listUnsubscribePost) &&
            listUnsubscribePost.Contains("One-Click", StringComparison.OrdinalIgnoreCase))
        {
            info.HasOneClick = true;
        }

        return info.HasAction ? info : null;
    }

    /// <summary>
    /// Parses a mailto: URI into recipient email address, subject, and body text.
    /// </summary>
    public static (string to, string subject, string body) ParseMailto(string mailtoUri)
    {
        if (string.IsNullOrWhiteSpace(mailtoUri)) return ("", "Unsubscribe", "");

        var s = mailtoUri.Trim();
        if (s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(7);

        var qIdx = s.IndexOf('?');
        var to = qIdx >= 0 ? s.Substring(0, qIdx) : s;
        var query = qIdx >= 0 ? s.Substring(qIdx + 1) : "";

        string subject = "Unsubscribe";
        string body = "";

        if (!string.IsNullOrEmpty(query))
        {
            var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var eq = p.IndexOf('=');
                if (eq > 0)
                {
                    var k = Uri.UnescapeDataString(p.Substring(0, eq)).Trim().ToLowerInvariant();
                    var v = Uri.UnescapeDataString(p.Substring(eq + 1)).Trim();
                    if (k == "subject") subject = v;
                    else if (k == "body") body = v;
                }
            }
        }

        return (Uri.UnescapeDataString(to).Trim(), subject, body);
    }

    /// <summary>
    /// Determines whether a message is identified as a newsletter or bulk mailing list.
    /// </summary>
    public static bool IsNewsletter(MailHeader h, RuleStore? ruleStore = null, MailContent? content = null)
    {
        if (!string.IsNullOrWhiteSpace(h.ListUnsubscribe)) return true;
        if (content != null && !string.IsNullOrWhiteSpace(content.ListUnsubscribe)) return true;

        if (h.Folder.Equals("Newsletters", StringComparison.OrdinalIgnoreCase) ||
            h.Folder.Equals("Bültenler", StringComparison.OrdinalIgnoreCase) ||
            h.Folder.Equals("Bultenler", StringComparison.OrdinalIgnoreCase))
            return true;

        var from = (h.FromAddress ?? "").ToLowerInvariant();
        if (from.Contains("news@") || from.Contains("newsletter@") || from.Contains("bulten@") ||
            from.Contains("bülten@") || from.Contains("duyuru@") || from.Contains("digest@") ||
            from.Contains("noreply@") || from.Contains("no-reply@") || from.Contains("updates@") ||
            from.Contains("info@news."))
            return true;

        var subj = (h.Subject ?? "").ToLowerInvariant();
        if (subj.Contains("newsletter") || subj.Contains("bülten") || subj.Contains("bülteni") ||
            subj.Contains("haftalık özet") || subj.Contains("weekly digest") || subj.Contains("monthly digest"))
            return true;

        if (ruleStore != null)
        {
            var hasSenderRule = ruleStore.Rules.Any(r =>
                r.Enabled &&
                r.Conditions.Any(c => c.Field == "from" && !string.IsNullOrEmpty(c.Value) &&
                                      (h.FromAddress ?? "").Contains(c.Value, StringComparison.OrdinalIgnoreCase)) &&
                r.Actions.Any(a => a.Type == "move_to_folder" && !string.IsNullOrEmpty(a.Value) &&
                                  (a.Value.Equals("Newsletters", StringComparison.OrdinalIgnoreCase) ||
                                   a.Value.Equals("Bültenler", StringComparison.OrdinalIgnoreCase))));
            if (hasSenderRule) return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a MailRule configured to automatically move messages from the sender to the Newsletters folder.
    /// </summary>
    public static MailRule CreateMoveToNewslettersRule(MailHeader h, string? folderName = null)
    {
        var target = folderName ?? (L.IsEnglish ? "Newsletters" : "Bültenler");
        var senderAddr = !string.IsNullOrWhiteSpace(h.FromAddress) ? h.FromAddress : h.FromName;
        return new MailRule
        {
            Name = L.IsEnglish
                ? $"Move {h.FromDisplay} to Newsletters"
                : $"{h.FromDisplay} gönderenini Bültenler klasörüne taşı",
            Enabled = true,
            MatchAll = true,
            StopProcessing = true,
            Conditions = {
                new RuleCondition { Field = "from", Value = senderAddr }
            },
            Actions = {
                new RuleAction { Type = "move_to_folder", Value = target }
            }
        };
    }
}

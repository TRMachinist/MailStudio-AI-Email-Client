using DnsClient;

namespace MailStudio.Services;

public enum DetectedProvider { Unknown, Google, Microsoft, Yandex }

/// <summary>Finds the service provider from the MX record of the email domain (also works for company domains).</summary>
public static class ProviderDetector
{
    public static async Task<(DetectedProvider provider, string mx)> DetectAsync(string email, CancellationToken ct = default)
    {
        var at = email.IndexOf('@');
        if (at < 0) return (DetectedProvider.Unknown, "");
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();

        switch (domain)
        {
            case "gmail.com" or "googlemail.com": return (DetectedProvider.Google, "gmail");
            case "outlook.com" or "hotmail.com" or "live.com" or "msn.com" or "outlook.com.tr" or "hotmail.com.tr":
                return (DetectedProvider.Microsoft, "outlook");
            case "yandex.com" or "yandex.com.tr" or "yandex.ru": return (DetectedProvider.Yandex, "yandex");
        }

        try
        {
            var dns = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(4), Retries = 1 });
            var r = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            var mx = r.Answers.MxRecords().OrderBy(m => m.Preference).Select(m => m.Exchange.Value.TrimEnd('.').ToLowerInvariant()).FirstOrDefault() ?? "";
            if (mx.EndsWith("google.com") || mx.EndsWith("googlemail.com")) return (DetectedProvider.Google, mx);
            if (mx.EndsWith("outlook.com") || mx.EndsWith("protection.outlook.com")) return (DetectedProvider.Microsoft, mx);
            if (mx.Contains("yandex")) return (DetectedProvider.Yandex, mx);
            return (DetectedProvider.Unknown, mx);
        }
        catch { return (DetectedProvider.Unknown, ""); }
    }
}

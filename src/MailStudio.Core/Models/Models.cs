using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MailStudio.Models;

public enum SecurityMode { SslOnConnect, StartTls, None }

/// <summary>Password: user/app password. Microsoft/Google: sign-in in the browser (XOAUTH2).</summary>
public enum AuthType { Password, Microsoft, Google }

/// <summary>Connection settings of a mail account. The password is stored encrypted with DPAPI.</summary>
public class AccountConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string UserName { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public AuthType AuthType { get; set; } = AuthType.Password;
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public SecurityMode ImapSecurity { get; set; } = SecurityMode.SslOnConnect;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public SecurityMode SmtpSecurity { get; set; } = SecurityMode.StartTls;
    public string Signature { get; set; } = "";
    public List<MailSignature> Signatures { get; set; } = new();
    public string? DefaultSignatureId { get; set; }
    public int InitialSyncCount { get; set; } = 300;   // legacy setting (backward compatibility)
    /// <summary>Outlook's "Mail to keep offline" period (months). 0 = all.</summary>
    public int OfflineMonths { get; set; } = 12;
    /// <summary>Also download attachments to this computer (like Outlook).</summary>
    public bool OfflineAttachments { get; set; } = true;
    /// <summary>Attachments larger than this (MB) are not downloaded automatically; they download on click.</summary>
    public int OfflineMaxAttachmentMB { get; set; } = 30;
}

public class MailSignature
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = L.T("İmza");
    public string Html { get; set; } = "";
}

public record AccountPreset(string Name, string ImapHost, int ImapPort, SecurityMode ImapSec,
                            string SmtpHost, int SmtpPort, SecurityMode SmtpSec, AuthType Auth, string Note)
{
    public override string ToString() => Name;

    public static readonly AccountPreset[] All =
    {
        new(L.T("Gmail (uygulama şifresi)"), "imap.gmail.com", 993, SecurityMode.SslOnConnect, "smtp.gmail.com", 587, SecurityMode.StartTls, AuthType.Password,
            L.T("Google hesabında 2 adımlı doğrulama açık olmalı. Şifre alanına 'Uygulama şifresi' gir (myaccount.google.com/apppasswords).")),
        new(L.T("Gmail (Google ile oturum aç)"), "imap.gmail.com", 993, SecurityMode.SslOnConnect, "smtp.gmail.com", 587, SecurityMode.StartTls, AuthType.Google,
            L.T("Kaydet'e basınca tarayıcıda Google giriş sayfası açılır; giriş yapıp izin verirsin, gerisi otomatik.")),
        new("Outlook.com / Hotmail", "outlook.office365.com", 993, SecurityMode.SslOnConnect, "smtp-mail.outlook.com", 587, SecurityMode.StartTls, AuthType.Microsoft,
            L.T("Kaydet'e basınca tarayıcıda Microsoft giriş sayfası açılır; giriş yapıp izin verirsin, gerisi otomatik.")),
        new("Microsoft 365 / Exchange", "outlook.office365.com", 993, SecurityMode.SslOnConnect, "smtp.office365.com", 587, SecurityMode.StartTls, AuthType.Microsoft,
            L.T("Kurumsal hesap: tarayıcıda şirket hesabınla giriş. Şirket IT'si üçüncü parti uygulama onayını kapattıysa yönetici onayı gerekir.")),
        new("Yandex", "imap.yandex.com", 993, SecurityMode.SslOnConnect, "smtp.yandex.com", 465, SecurityMode.SslOnConnect, AuthType.Password,
            L.T("Yandex ID > Güvenlik > Uygulama şifreleri bölümünden mail için şifre oluştur.")),
        new(L.T("Diğer IMAP"), "", 993, SecurityMode.SslOnConnect, "", 587, SecurityMode.StartTls, AuthType.Password,
            L.T("Sunucu bilgilerini mail sağlayıcından al.")),
    };
}

/// <summary>Folder tree node in the left pane.</summary>
public partial class FolderNode : ObservableObject
{
    public string AccountId { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public bool IsAccountRoot { get; init; }
    public int SortOrder { get; init; } = 50;

    [ObservableProperty] private int unreadCount;
    [ObservableProperty] private bool isExpanded = true;

    public ObservableCollection<FolderNode> Children { get; } = new();

    public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString() : "";
    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(UnreadText));
}

/// <summary>A row in the message list (header information).</summary>
public partial class MailHeader : ObservableObject
{
    public string AccountId { get; init; } = "";
    public string Folder { get; init; } = "";
    public uint Uid { get; init; }
    public uint UidValidity { get; init; }
    public bool IsDraft { get; init; }
    public string ReplyTo { get; init; } = "";
    public string InReplyTo { get; init; } = "";
    public List<string> References { get; init; } = new();
    public string ConversationKey { get; set; } = "";
    public string MessageId { get; init; } = "";
    public string FromName { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string To { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Preview { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public bool HasAttachments { get; init; }

    [ObservableProperty] private bool isRead;
    [ObservableProperty] private bool isFlagged;

    // ---- Conditional formatting (Outlook "Conditional Formatting") ----
    [ObservableProperty] private string? formatFore;
    [ObservableProperty] private string? formatBack;
    [ObservableProperty] private bool formatBold;
    [ObservableProperty] private bool formatItalic;
    [ObservableProperty] private string? formatRuleName;
    public bool HasFormatFore => !string.IsNullOrEmpty(FormatFore);
    partial void OnFormatForeChanged(string? value) => OnPropertyChanged(nameof(HasFormatFore));

    // ---- Conversation view ----
    [ObservableProperty] private int threadCount = 1;
    [ObservableProperty] private bool isThreadChild;
    [ObservableProperty] private bool isThreadExpanded;
    [ObservableProperty] private bool threadHasUnread;
    public string? GroupOverride { get; set; }
    public string DisplayGroup => GroupOverride ?? GroupName;
    public bool IsThreadHead => ThreadCount > 1 && !IsThreadChild;
    public string ThreadArrow => IsThreadExpanded ? "" : "";
    public string ThreadCountText => ThreadCount > 1 ? $"({ThreadCount})" : "";
    partial void OnThreadCountChanged(int value) { OnPropertyChanged(nameof(IsThreadHead)); OnPropertyChanged(nameof(ThreadCountText)); }
    partial void OnIsThreadChildChanged(bool value) => OnPropertyChanged(nameof(IsThreadHead));
    partial void OnIsThreadExpandedChanged(bool value) => OnPropertyChanged(nameof(ThreadArrow));

    /// <summary>Conversation key: lower-case subject with RE:/FW:/YNT:/İLT: prefixes removed.</summary>
    public string ThreadKey
    {
        get
        {
            var s = System.Text.RegularExpressions.Regex.Replace(Subject ?? "",
                @"^(\s*(re|fw|fwd|ynt|yanıt|ilt|i̇lt|İlt|ilet|aw|wg|sv|tr|cevap)\s*(\[\d+\])?\s*:\s*)+", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s.Trim().ToLowerInvariant();
        }
    }

    public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? L.T("(Konu yok)") : Subject;

    public string DateDisplay
    {
        get
        {
            var local = Date.ToLocalTime();
            var today = DateTime.Today;
            if (local.Date == today) return local.ToString("HH:mm");
            if (local.Date > today.AddDays(-7)) return local.ToString("ddd HH:mm");
            return local.ToString("d");
        }
    }

    public string GroupName
    {
        get
        {
            var d = Date.ToLocalTime().Date;
            var today = DateTime.Today;
            if (d == today) return L.T("Bugün");
            if (d == today.AddDays(-1)) return L.T("Dün");
            int diff = ((int)today.DayOfWeek + 6) % 7; // week starts on Monday
            if (d >= today.AddDays(-diff)) return L.T("Bu Hafta");
            if (d >= today.AddDays(-diff - 7)) return L.T("Geçen Hafta");
            if (d.Year == today.Year && d.Month == today.Month) return L.T("Bu Ay");
            return L.T("Daha Eski");
        }
    }
}

/// <summary>Full message loaded into the reading pane.</summary>
public class MailContent
{
    /// <summary>A copy stored by the old decoder is kept; it must be refreshed once from the source MIME.</summary>
    public bool NeedsEncodingRefresh { get; init; }
    public string Html { get; init; } = "";
    public string Text { get; init; } = "";
    public string Cc { get; init; } = "";
    public string ReplyTo { get; init; } = "";
    public List<string> References { get; init; } = new();
    public List<AttachmentInfo> Attachments { get; init; } = new();
}

public class AttachmentInfo
{
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    /// <summary>IMAP body part number (e.g. "2", "1.3"); clicking an attachment downloads only this part.</summary>
    public string Part { get; init; } = "";
    public string SizeDisplay => Size > 1024 * 1024 ? $"{Size / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, Size / 1024)} KB";
}

public class ComposeRequest
{
    public string? EditableHtml { get; set; }
    public string? SignatureHtml { get; set; }
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public string? SubmissionId { get; set; }
    public string? SubmissionState { get; set; }
    public string DraftId { get; set; } = Guid.NewGuid().ToString("N");
    public string? DraftAccountId { get; set; }
    public string? DraftFolder { get; set; }
    public uint? DraftUid { get; set; }
    public uint? DraftUidValidity { get; set; }
    public List<string> References { get; set; } = new();
    public string Bcc { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Formatted body (HTML). If set it is sent instead of Body; Body is its plain-text version.</summary>
    public string? BodyHtml { get; set; }
    /// <summary>Quoted original message (HTML) below a reply/forward.</summary>
    public string? QuotedHtml { get; set; }
    public string? InReplyTo { get; set; }
    public List<string> AttachmentPaths { get; set; } = new();
}

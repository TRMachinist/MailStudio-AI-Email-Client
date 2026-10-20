using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views;

public partial class AccountWindow : Window
{
    readonly AccountConfig? _existing;
    readonly AppSettings _settings;
    readonly OAuthService _oauth;
    public AccountConfig? Result { get; private set; }

    public AccountWindow(AccountConfig? existing, AppSettings settings, OAuthService oauth)
    {
        InitializeComponent();
        Services.UiTranslator.Apply(this);
        _existing = existing;
        _settings = settings;
        _oauth = oauth;
        foreach (var p in AccountPreset.All) PresetBox.Items.Add(p);
        foreach (var box in new[] { ImapSecBox, SmtpSecBox })
            foreach (var m in Enum.GetValues<SecurityMode>()) box.Items.Add(m);

        MsClientBox.Text = settings.MicrosoftClientId;
        GClientBox.Text = settings.GoogleClientId;
        GSecretBox.Text = settings.GoogleClientSecret;

        if (existing == null)
        {
            PresetBox.SelectedIndex = 0;
            SyncCountBox.Text = "300";
            OfflineBox.SelectedIndex = 3; // 1 year (Outlook default)
        }
        else
        {
            Title = HeaderText.Text = L.T("Hesap Ayarları");
            PresetBox.SelectedItem = AccountPreset.All.FirstOrDefault(p => p.ImapHost == existing.ImapHost && p.Auth == existing.AuthType)
                                     ?? AccountPreset.All[^1];
            NameBox.Text = existing.DisplayName;
            EmailBox.Text = existing.Email;
            UserBox.Text = existing.UserName;
            AuthBox.SelectedIndex = (int)existing.AuthType;
            ImapHostBox.Text = existing.ImapHost;
            ImapPortBox.Text = existing.ImapPort.ToString();
            ImapSecBox.SelectedItem = existing.ImapSecurity;
            SmtpHostBox.Text = existing.SmtpHost;
            SmtpPortBox.Text = existing.SmtpPort.ToString();
            SmtpSecBox.SelectedItem = existing.SmtpSecurity;
            SignatureBox.Text = existing.Signature;
            SyncCountBox.Text = existing.InitialSyncCount.ToString();
            OfflineBox.SelectedItem = OfflineBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == existing.OfflineMonths.ToString()) ?? OfflineBox.Items[3];
            OfflineAttCheck.IsChecked = existing.OfflineAttachments;
            PasswordLabel.Text = L.T("Şifre (değiştirmek için doldur)");
            CalendarSyncCheck.IsChecked = existing.EnableCalendarSync;
            CalDavUrlBox.Text = existing.CalDavUrl;
            CalendarExpander.IsExpanded = existing.EnableCalendarSync;
        }
        UpdateCalendarSyncUi();
    }

    AuthType SelectedAuth => (AuthType)Math.Max(0, AuthBox.SelectedIndex);

    void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is not AccountPreset p) return;
        PresetNote.Text = p.Note;
        if (_existing != null && _existing.ImapHost == p.ImapHost && _existing.AuthType == p.Auth) return;
        AuthBox.SelectedIndex = (int)p.Auth;
        ImapHostBox.Text = p.ImapHost;
        ImapPortBox.Text = p.ImapPort.ToString();
        ImapSecBox.SelectedItem = p.ImapSec;
        SmtpHostBox.Text = p.SmtpHost;
        SmtpPortBox.Text = p.SmtpPort.ToString();
        SmtpSecBox.SelectedItem = p.SmtpSec;
        ServerExpander.IsExpanded = string.IsNullOrEmpty(p.ImapHost);
    }

    void OnAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordGrid == null) return;
        ResultText.Text = "";
        var oauth = SelectedAuth != AuthType.Password;
        PasswordGrid.Visibility = oauth ? Visibility.Collapsed : Visibility.Visible;
        // If the application ID is already defined the user sees no technical field
        var missingId = SelectedAuth == AuthType.Microsoft ? MsClientBox.Text.Length == 0
                      : SelectedAuth == AuthType.Google && (GClientBox.Text.Length == 0 || GSecretBox.Text.Length == 0);
        OAuthExpander.Visibility = oauth && missingId ? Visibility.Visible : Visibility.Collapsed;
        OAuthExpander.IsExpanded = oauth && missingId;
        SaveButton.Content = oauth ? L.T("Oturum Aç ve Kaydet") : L.T("Kaydet");
        TestButton.Content = oauth ? L.T("Oturum Aç / Sına") : L.T("Bağlantıyı Sına");
        UpdateCalendarSyncUi();
    }

    void OnCalendarSyncCheckChanged(object sender, RoutedEventArgs e) => UpdateCalendarSyncUi();

    void UpdateCalendarSyncUi()
    {
        if (CalendarConsentNotice == null || CalDavGrid == null) return;
        var enabled = CalendarSyncCheck.IsChecked == true;
        var oauth = SelectedAuth == AuthType.Microsoft || SelectedAuth == AuthType.Google;
        CalendarConsentNotice.Visibility = enabled && oauth ? Visibility.Visible : Visibility.Collapsed;
        CalDavGrid.Visibility = enabled && !oauth ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnOAuthHelp(object sender, RoutedEventArgs e)
    {
        var doc = Path.Combine(AppContext.BaseDirectory, "docs", L.IsEnglish ? "OAUTH_SETUP.md" : "OAUTH_KURULUM.md");
        try { Process.Start(new ProcessStartInfo(File.Exists(doc) ? doc : "https://github.com/TRMachinist/MailStudio-AI-Email-Client/blob/main/docs/OAUTH_SETUP.md") { UseShellExecute = true }); }
        catch { Process.Start("notepad.exe", doc); }
    }

    string _detectedFor = "";
    Task _detectTask = Task.CompletedTask;

    void OnEmailLostFocus(object sender, RoutedEventArgs e) => _detectTask = DetectAsync();

    /// <summary>If detection has not finished when the button is pressed, wait for it first.</summary>
    async Task EnsureDetectedAsync()
    {
        await _detectTask;
        if (EmailBox.Text.Trim() != _detectedFor) await (_detectTask = DetectAsync());
    }

    async Task DetectAsync()
    {
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@') || email == _detectedFor || _existing != null) return;
        _detectedFor = email;
        ResultText.Text = "";

        // Find the provider from the domain's MX record (e.g. someone@company.com → Google Workspace)
        var (prov, mx) = await ProviderDetector.DetectAsync(email);
        var currentAuth = SelectedAuth;
        AccountPreset? p = prov switch
        {
            // Like BlueMail: direct Google sign-in instead of a password for a Google address
            DetectedProvider.Google => AccountPreset.All.First(x => x.ImapHost == "imap.gmail.com" && x.Auth == AuthType.Google),
            DetectedProvider.Microsoft => AccountPreset.All.First(x => x.Auth == AuthType.Microsoft &&
                                         (email.EndsWith("outlook.com") || email.EndsWith("hotmail.com") || email.EndsWith("live.com")
                                             ? x.SmtpHost.Contains("smtp-mail") : x.SmtpHost == "smtp.office365.com")),
            DetectedProvider.Yandex => AccountPreset.All.First(x => x.ImapHost.Contains("yandex")),
            _ => null
        };
        if (p != null)
        {
            PresetBox.SelectedItem = p;
            var what = prov switch
            {
                DetectedProvider.Google => L.T("Google (Gmail / Google Workspace)"),
                DetectedProvider.Microsoft => "Microsoft (Outlook / Microsoft 365)",
                _ => "Yandex"
            };
            PresetNote.Text = L.F("Bu adresin maili {0} üzerinde (MX: {1}). Sunucu ayarları otomatik dolduruldu.\n", what, mx) + p.Note;
        }
        else if (PresetBox.SelectedItem is AccountPreset { ImapHost: "" })
        {
            var domain = email.Split('@')[1].Trim();
            if (ImapHostBox.Text.Length == 0) ImapHostBox.Text = "imap." + domain;
            if (SmtpHostBox.Text.Length == 0) SmtpHostBox.Text = "smtp." + domain;
        }
    }

    AccountConfig? Build()
    {
        if (!EmailBox.Text.Contains('@')) { Fail(L.T("Geçerli bir e-posta adresi girin.")); return null; }
        if (ImapHostBox.Text.Trim().Length == 0 || SmtpHostBox.Text.Trim().Length == 0) { Fail(L.T("Sunucu bilgileri eksik.")); ServerExpander.IsExpanded = true; return null; }
        var auth = SelectedAuth;
        if (auth == AuthType.Password && _existing?.AuthType != AuthType.Password && PasswordBox.Password.Length == 0) { Fail(L.T("Şifre girin.")); return null; }
        if (auth == AuthType.Microsoft && MsClientBox.Text.Trim().Length == 0) { Fail(L.T("Microsoft uygulama kimliğini girin.")); OAuthExpander.IsExpanded = true; return null; }
        if (auth == AuthType.Google && (GClientBox.Text.Trim().Length == 0 || GSecretBox.Text.Trim().Length == 0)) { Fail(L.T("Google istemci kimliği ve gizli anahtarını girin.")); OAuthExpander.IsExpanded = true; return null; }
        if (!int.TryParse(ImapPortBox.Text, out var ip) || !int.TryParse(SmtpPortBox.Text, out var sp)) { Fail(L.T("Port sayı olmalı.")); return null; }

        // OAuth IDs are global settings
        _settings.MicrosoftClientId = MsClientBox.Text.Trim();
        _settings.GoogleClientId = GClientBox.Text.Trim();
        _settings.GoogleClientSecret = GSecretBox.Text.Trim();
        _settings.Save();

        return new AccountConfig
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            DisplayName = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : EmailBox.Text.Split('@')[0],
            Email = EmailBox.Text.Trim(),
            UserName = UserBox.Text.Trim(),
            AuthType = auth,
            ProtectedPassword = auth != AuthType.Password ? "" :
                                PasswordBox.Password.Length > 0 ? AccountStore.Protect(PasswordBox.Password) : _existing!.ProtectedPassword,
            ImapHost = ImapHostBox.Text.Trim(),
            ImapPort = ip,
            ImapSecurity = (SecurityMode)(ImapSecBox.SelectedItem ?? SecurityMode.SslOnConnect),
            SmtpHost = SmtpHostBox.Text.Trim(),
            SmtpPort = sp,
            SmtpSecurity = (SecurityMode)(SmtpSecBox.SelectedItem ?? SecurityMode.StartTls),
            Signature = SignatureBox.Text,
            InitialSyncCount = int.TryParse(SyncCountBox.Text, out var n) && n > 0 ? n : 300,
            OfflineMonths = int.Parse((string)((OfflineBox.SelectedItem as ComboBoxItem)?.Tag ?? "12")),
            OfflineAttachments = OfflineAttCheck.IsChecked == true,
            OfflineMaxAttachmentMB = _existing?.OfflineMaxAttachmentMB ?? 30,
            EnableCalendarSync = CalendarSyncCheck.IsChecked == true,
            CalDavUrl = CalDavUrlBox.Text.Trim(),
            CalDavUser = _existing?.CalDavUser ?? "",
            CalDavPassword = _existing?.CalDavPassword ?? "",
            LastCalendarSyncUtc = _existing?.LastCalendarSyncUtc,
            CalendarSyncStatus = _existing?.CalendarSyncStatus,
        };
    }

    async Task<bool> TestAsync(AccountConfig a)
    {
        TestButton.IsEnabled = SaveButton.IsEnabled = false;
        ResultText.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        ResultText.Text = a.AuthType == AuthType.Password ? L.T("Sunuculara bağlanılıyor...") : L.T("Tarayıcıda oturum açın, sonra buraya dönün...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await MailService.TestAsync(a, _oauth, cts.Token);
            ResultText.SetResourceReference(ForegroundProperty, "StatusSuccessBrush");
            ResultText.Text = L.T("✔ IMAP ve SMTP bağlantısı başarılı.");
            return true;
        }
        catch (Exception ex)
        {
            var raw = ex.Message;
            var m = raw;
            var google = a.ImapHost.Contains("gmail", StringComparison.OrdinalIgnoreCase);
            if (raw.Contains("Application-specific password", StringComparison.OrdinalIgnoreCase))
                m = L.T("Google uygulama şifresi istiyor: myaccount.google.com → Güvenlik → 2 Adımlı Doğrulama → Uygulama şifreleri. 16 haneli şifreyi boşluksuz gir.");
            else if (raw.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) || raw.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase)
                     || raw.Contains("535", StringComparison.Ordinal))
                m = a.AuthType != AuthType.Password
                    ? L.T("Sunucu jetonu reddetti. Microsoft 365'te IMAP kapalı olabilir veya uygulama izinleri (IMAP.AccessAsUser.All, SMTP.Send) eksik.")
                    : google
                        ? L.T("Google normal şifreyi kabul etmiyor. Uygulama şifresi gir (myaccount.google.com/apppasswords). Menü yoksa şirket yöneticisi kapatmıştır → 'Google ile oturum aç' kullan.")
                        : L.T("Giriş reddedildi. Kullanıcı adı/şifreyi veya sağlayıcının 'uygulama şifresi' gerekip gerekmediğini kontrol et.");
            else if (raw.Contains("IMAP", StringComparison.OrdinalIgnoreCase) && raw.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                m = L.T("Bu hesapta IMAP kapalı. Gmail ayarları → Yönlendirme ve POP/IMAP → IMAP'i etkinleştir (Workspace'te yönetici açmalı).");
            Fail(m + (m == raw ? "" : $"\n(Sunucu: {(raw.Length > 140 ? raw[..140] + "…" : raw)})"));
            return false;
        }
        finally { TestButton.IsEnabled = SaveButton.IsEnabled = true; }
    }

    async void OnTest(object sender, RoutedEventArgs e)
    {
        await EnsureDetectedAsync();
        var a = Build();
        if (a != null) await TestAsync(a);
    }

    async void OnSave(object sender, RoutedEventArgs e)
    {
        await EnsureDetectedAsync();
        var a = Build();
        if (a == null) return;
        if (!await TestAsync(a) &&
            MessageBox.Show(this, L.T("Bağlantı sınaması başarısız. Yine de kaydedilsin mi?"), L.T("Hesap"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Result = a;
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    void Fail(string msg)
    {
        ResultText.SetResourceReference(ForegroundProperty, "StatusDangerBrush");
        ResultText.Text = msg;
    }
}

/// <summary>Label in the user's language for the security mode.</summary>
public sealed class SecurityLabel : System.Windows.Data.IValueConverter
{
    public static readonly SecurityLabel Instance = new();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value switch
    {
        SecurityMode.SslOnConnect => "SSL / TLS",
        SecurityMode.StartTls => "STARTTLS",
        SecurityMode.None => L.T("Yok (güvensiz)"),
        _ => value?.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

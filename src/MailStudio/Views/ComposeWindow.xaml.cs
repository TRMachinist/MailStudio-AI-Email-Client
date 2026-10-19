using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MailStudio.Views;

public class FileNameConverter : IValueConverter
{
    public static readonly FileNameConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => Path.GetFileName(value as string ?? "");
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Outlook-style new message window: rich (HTML) editor, attachments, signature, contact completion and writing with AI.</summary>
public partial class ComposeWindow : Window
{
    readonly MailService _mail;
    readonly ComposeAiOptions? _aiOptions;
    readonly Action? _saveAccounts;
    Task? _editorInitialization;
    public bool IsInline { get; private set; }
    public string? FromAccountId => From?.Id;
    public event EventHandler? PopOutRequested;
    Window DialogOwner => Window.GetWindow(Editor) ?? Owner ?? this;
    public async Task<ComposeRequest> SnapshotAsync() => await CollectAsync();
    public void CloseForTransfer() { _closingConfirmed=true; Close(); }
    public FrameworkElement DetachForInline()
    {
        IsInline=true;ComposeRibbon.Visibility=Visibility.Collapsed;ComposeTools.Visibility=PopOutButton.Visibility=InlineCloseButton.Visibility=InlineAiInstruction.Visibility=InlineAiWriteButton.Visibility=Visibility.Visible;
        var body=(FrameworkElement)Content;Content=null;return body;
    }
    public async Task StartInlineAsync(){await EnsureEditorAsync();if(!_closed)_autosave.Start();}
    Task EnsureEditorAsync()=>_editorInitialization??=InitEditorAsync();
    void OnPopOut(object sender,RoutedEventArgs e)=>PopOutRequested?.Invoke(this,EventArgs.Empty);
    async void OnInlineClose(object sender,RoutedEventArgs e)=>await RequestCloseAsync();
    void OnInlineAiWrite(object sender,RoutedEventArgs e){AiInstruction.Text=InlineAiInstruction.Text;OnAiWrite(sender,e);}
    readonly Func<string, CancellationToken, Task<string>> _ai;
    readonly ComposeRequest _req;
    readonly ObservableCollection<string> _attachments = new();
    CancellationTokenSource? _aiCts;
    readonly CancellationTokenSource _lifetime = new();
    readonly Func<MessageBoxResult> _confirmClose;
    bool _closed;
    bool _allowRemoteImages;
    async void OnLoadComposeImages(object sender,RoutedEventArgs e){_allowRemoteImages=true;await Js("ms.reloadImages()");StatusText.Text=L.T("Bu taslak için internet resimleri etkin.");}
    TaskCompletionSource<bool>? _closeResult;
    bool _editorReady, _contentReady, _sent, _closingConfirmed, _initializing = true, _closePrompt, _uncertain;
    public bool IsEditorInitialized => _contentReady && !_closed;
    readonly ComposeState _state = new();
    readonly LocalDraftStore _drafts = new();
    readonly System.Windows.Threading.DispatcherTimer _autosave = new();
    public SendResult? LastSendResult { get; private set; }
    public Action<AccountConfig, ComposeRequest, int>? UndoSendAction { get; set; }
    public Action<AccountConfig, ComposeRequest, DateTimeOffset>? ScheduleSendAction { get; set; }
    public int FollowUpDays => FollowUpCheck?.IsChecked == true && FollowUpDaysBox?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag as string, out var d) ? d : 0;
    public string SubjectText => SubjectBox?.Text ?? "";
    public string ToAddressText => ToBox?.Text ?? "";
    void OnFollowUpCheckChanged(object sender, RoutedEventArgs e)
    {
        if (FollowUpDaysBox != null) FollowUpDaysBox.IsEnabled = FollowUpCheck?.IsChecked == true;
        Changed();
    }
    void Changed() { if (!_initializing) _state.Changed(); }
    void SetBusy(bool busy)
    {
        ComposeRibbon.IsEnabled = !busy;
        ComposeTools.IsEnabled = !busy;
        SendButton.IsEnabled = SendMenuButton.IsEnabled = !busy && _editorReady && !_uncertain;
        FromBox.IsEnabled = ToBox.IsEnabled = CcBox.IsEnabled = BccBox.IsEnabled = SubjectBox.IsEnabled = !busy;
        Editor.IsEnabled = !busy; AttachList.IsEnabled = !busy;
    }

    public event EventHandler? Sent;

    public ComposeWindow(IReadOnlyList<AccountConfig> accounts, string accountId, ComposeRequest req, MailService mail,
                         Func<string, CancellationToken, Task<string>> ai, Func<string> providerName,
                         Func<MessageBoxResult>? confirmClose = null, ComposeAiOptions? aiOptions = null, Action? saveAccounts = null)
    {
        InitializeComponent();
        Services.UiTranslator.Apply(this);
        Editor.DefaultBackgroundColor = ThemeManager.IsDark ? System.Drawing.Color.FromArgb(0x1E, 0x29, 0x3B) : System.Drawing.Color.White;
        _mail = mail; _aiOptions=aiOptions; _saveAccounts=saveAccounts;
        _ai = ai;
        _req = req;
        _confirmClose = confirmClose ?? (() => MessageBox.Show(DialogOwner, L.T("Değişiklikleri taslak olarak kaydetmek istiyor musunuz?"), "MailStudio",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question));
        _uncertain = req.SubmissionState is "Sending" or "Uncertain" or "Accepted" or "Completed";

        FromBox.ItemsSource = accounts;
        FromBox.SelectedItem = accounts.FirstOrDefault(a => a.Id == accountId) ?? accounts.FirstOrDefault();
        ToBox.Text = req.To;
        CcBox.Text = req.Cc;
        BccBox.Text = req.Bcc;
        SubjectBox.Text = req.Subject;
        foreach (var p in req.AttachmentPaths) _attachments.Add(p);
        AttachList.ItemsSource = _attachments;
        AiProviderText.Text = "AI: " + providerName();
        ComposeProviderBox.ItemsSource=aiOptions?.Providers.Select(p=>p.Name).ToArray() ?? new[]{providerName()};
        ComposeProviderBox.SelectedItem=req.AiProvider??aiOptions?.Provider ?? providerName();
        ComposeModelBox.Text=req.AiModel??aiOptions?.Model ?? "";
        FontBox.SelectedIndex = 0;
        SizeBox.Text = "11";
        if (req.FollowUpDays > 0)
        {
            FollowUpCheck.IsChecked = true;
            FollowUpDaysBox.IsEnabled = true;
            foreach (ComboBoxItem item in FollowUpDaysBox.Items)
            {
                if (item.Tag is string s && int.TryParse(s, out var d) && d == req.FollowUpDays)
                {
                    FollowUpDaysBox.SelectedItem = item;
                    break;
                }
            }
        }

        _initializing = false;
        ToBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed()));
        CcBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed()));
        BccBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed()));
        FromBox.SelectionChanged += async (_, _) => {Changed();if(_editorReady&&!_state.IsBusy)await Js($"ms.setSignature({JsonSerializer.Serialize(SignatureHtml(DefaultSignature()))})");};
        _attachments.CollectionChanged += (_, _) => Changed();
        _autosave.Interval = TimeSpan.FromSeconds(10);
        _autosave.Tick += async (_, _) =>
        {
            if (!_contentReady || _aiCts!=null || !_state.IsDirty || From is not { } account || !_state.TryBegin()) return;
            try { _drafts.Save(account.Id, await CollectAsync()); }
            catch (Exception ex) { StatusText.Text = L.T("Yerel kurtarma kopyası kaydedilemedi: ") + ex.Message; }
            finally { _state.End(); }
        };
        SendButton.IsEnabled = SendMenuButton.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            try { await EnsureEditorAsync(); if (!_closed) _autosave.Start(); }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception ex) { if (!_closed) StatusText.Text = L.T("Editör açılamadı: ") + ex.Message; }
        };
        Closed += (_, _) => { _closed = true; CompleteCloseRequest(true); _editorReady = false; _lifetime.Cancel(); _autosave.Stop(); _aiCts?.Cancel(); Editor.Dispose(); };
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { OnSend(this, e); e.Handled = true; }
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { OnSaveDraft(this, e); e.Handled = true; }
        };
    }

    AccountConfig? From => FromBox.SelectedItem as AccountConfig;

    // ================================================================ Editor

    async Task InitEditorAsync()
    {
        await Editor.EnsureCoreWebView2Async(await MailHtml.EnvAsync());
        if (_closed) return;
        var core = Editor.CoreWebView2;
        core.Profile.PreferredColorScheme = ThemeManager.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !e.Request.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                && !(_allowRemoteImages && e.ResourceContext==CoreWebView2WebResourceContext.Image && (e.Request.Uri.StartsWith("https:",StringComparison.OrdinalIgnoreCase)||e.Request.Uri.StartsWith("http:",StringComparison.OrdinalIgnoreCase))))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NavigationStarting += (s, a) =>
        {
            // Links inside the editor must not change the page
            if (_editorReady) a.Cancel = true;
        };
        core.NewWindowRequested += (s, a) => a.Handled = true;
        core.WebMessageReceived += (s, a) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(a.TryGetWebMessageAsString());
                var t = doc.RootElement.GetProperty("t").GetString();
                if (_closed || _closingConfirmed) return;
                if (t == "files")
                {
                    // File paths come from native DOM File objects, never from a message-supplied string.
                    AddAttachmentFiles(a.AdditionalObjects.OfType<CoreWebView2File>().Select(file => file.Path));
                }
                else if (t == "files-unavailable") StatusText.Text = L.T("Dosya bırakma desteklenemedi; Dosya Ekle düğmesini kullanabilirsiniz.");
                else if (t == "dirty") Changed();
                else if (t == "paste" && !_state.IsBusy)
                    _ = Js($"ms.insertHtml({JsonSerializer.Serialize(EditorContent.Sanitize(doc.RootElement.GetProperty("html").GetString()))})");
                else if (t == "state")
                {
                    Mark(BoldBtn, doc.RootElement.GetProperty("b").GetBoolean());
                    Mark(ItalicBtn, doc.RootElement.GetProperty("i").GetBoolean());
                    Mark(UnderBtn, doc.RootElement.GetProperty("u").GetBoolean());
                }
            }
            catch (Exception ex) { if (!_closed) StatusText.Text = L.T("Editör işlemi tamamlanamadı: ") + ex.Message; }
        };
        var tcs = new TaskCompletionSource();
        core.NavigationCompleted += (_, _) => tcs.TrySetResult();
        Editor.NavigateToString(EditorHtml.Page);
        await tcs.Task.WaitAsync(_lifetime.Token);
        if (_closed) return;
        _editorReady = true;

        var draft = _req.EditableHtml ?? (!string.IsNullOrEmpty(_req.BodyHtml) ? _req.BodyHtml : TextToHtml(_req.Body));
        await Js($"ms.setDraft({JsonSerializer.Serialize(EditorContent.Sanitize(draft))})");
        if (!string.IsNullOrEmpty(_req.QuotedHtml)) await Js($"ms.setQuote({JsonSerializer.Serialize(EditorContent.Sanitize(_req.QuotedHtml))})");
        var initialSignature=_req.SignatureHtml;
        if(initialSignature==null && _req.DraftFolder==null) initialSignature=DefaultSignature();
        if(!string.IsNullOrWhiteSpace(initialSignature))await Js($"ms.setSignature({JsonSerializer.Serialize(SignatureHtml(initialSignature))})");
        if (_closed || _closingConfirmed) return;
        _contentReady=true;
        SetBusy(false);
        if (_uncertain) StatusText.Text = L.T("Bu taslak daha önce gönderime alındı. Yeniden gönderim kapalı; Dosya menüsünden gönderim kayıtlarını kontrol edin. Kayıt: ") + _req.SubmissionId;
        if (string.IsNullOrEmpty(_req.DraftFolder) && (!string.IsNullOrEmpty(_req.Body) || !string.IsNullOrEmpty(_req.BodyHtml) || !string.IsNullOrEmpty(_req.QuotedHtml) || !string.IsNullOrEmpty(_req.To) || !string.IsNullOrEmpty(_req.Cc) || !string.IsNullOrEmpty(_req.Bcc) || !string.IsNullOrEmpty(_req.Subject) || _attachments.Count > 0)) Changed();

        if (string.IsNullOrWhiteSpace(ToBox.Text)) ToBox.Focus();
        else { Editor.Focus(); await Js("ms.focusStart()"); }
    }

    static void Mark(Button b, bool on) { if (on) b.SetResourceReference(BackgroundProperty, "AccentSubtleBrush"); else b.Background = Brushes.Transparent; }

    async Task<string> Js(string script)
    {
        if (_closed || _closingConfirmed || !_editorReady) return "";
        if (_state.IsBusy && script != "ms.snapshot()") return "";
        var raw = await Editor.CoreWebView2.ExecuteScriptAsync(script);
        if (string.IsNullOrEmpty(raw) || raw == "null") return "";
        try { return JsonSerializer.Deserialize<string>(raw) ?? ""; } catch { return raw; }
    }

    static string TextToHtml(string text) =>
        string.IsNullOrEmpty(text) ? "<div><br></div>" :
        string.Join("", text.Replace("\r\n", "\n").Split('\n').Select(l => "<div>" + (l.Length == 0 ? "<br>" : WebUtility.HtmlEncode(l)) + "</div>"));

    static string SignatureHtml(string sig) =>
        sig.TrimStart().StartsWith("<") ? EditorContent.Sanitize(sig) : "<div style=\"color:#444\">" + TextToHtml(sig) + "</div>";

    async void OnExec(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        if (sender is Button { Tag: string cmd }) await Js($"ms.exec('{cmd}')");
    }

    async void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_editorReady || FontBox.SelectedItem is not ComboBoxItem { Content: string f }) return;
        await Js($"ms.exec('fontName', {JsonSerializer.Serialize(f)})");
    }

    async void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_editorReady || SizeBox.SelectedItem is not ComboBoxItem { Content: string s }) return;
        await Js($"ms.fontSize({int.Parse(s)})");
    }

    void OnColorMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string cmd } btn) return;
        var colors = cmd == "foreColor"
            ? new[] { "#000000", "#444444", "#C00000", "#FF0000", "#FFC000", "#00B050", "#0070C0", "#002060", "#7030A0" }
            : new[] { "#FFFF00", "#00FF00", "#00FFFF", "#FF00FF", "#FFC7CE", "#C6EFCE", "#DDEBF7", "#FFFFFF" };
        var menu = new ContextMenu { PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var c in colors)
        {
            var item = new MenuItem
            {
                Header = new Border { Width = 70, Height = 16, CornerRadius = new CornerRadius(3), Background = (Brush)new BrushConverter().ConvertFromString(c)!, BorderBrush = ThemeManager.Brush("BorderStrongBrush"), BorderThickness = new Thickness(1) }
            };
            var color = c;
            item.Click += async (_, _) => await Js($"ms.exec('{cmd}', '{color}')");
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- Clipboard (via the Windows clipboard, because execCommand('paste') is disabled in WebView2) ----

    async void OnPaste(object sender, RoutedEventArgs e)
    {
        string? html = null;
        if (Clipboard.ContainsImage())
        {
            var img = Clipboard.GetImage();
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            enc.Save(ms);
            html = $"<img src=\"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}\">";
        }
        else if (Clipboard.ContainsText(TextDataFormat.Html))
        {
            var raw = Clipboard.GetText(TextDataFormat.Html);
            var s = raw.IndexOf("<!--StartFragment-->", StringComparison.Ordinal);
            var t = raw.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
            html = s >= 0 && t > s ? raw[(s + 20)..t] : null;
        }
        if (html == null && Clipboard.ContainsText()) html = TextToHtml(Clipboard.GetText());
        if (html != null) await Js($"ms.insertHtml({JsonSerializer.Serialize(EditorContent.Sanitize(html))})");
    }

    async void OnCopy(object sender, RoutedEventArgs e) => await CopySelectionAsync(cut: false);
    async void OnCut(object sender, RoutedEventArgs e) => await CopySelectionAsync(cut: true);

    async Task CopySelectionAsync(bool cut)
    {
        var text = await Js("window.getSelection().toString()");
        if (text.Length == 0) return;
        Clipboard.SetText(text);
        if (cut) await Js("ms.exec('delete')");
    }

    // ---- Ekle ----

    async void OnInsertImage(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        var dlg = new OpenFileDialog { Filter = "Resimler|*.png;*.jpg;*.jpeg;*.gif;*.bmp", Title = L.T("Resim ekle") };
        if (dlg.ShowDialog(DialogOwner) != true) return;
        var ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant().Replace("jpg", "jpeg");
        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(dlg.FileName));
        await Js($"ms.insertHtml({JsonSerializer.Serialize($"<img src=\"data:image/{ext};base64,{data}\">")})");
    }

    async void OnInsertLink(object sender, RoutedEventArgs e)
    {
        var url = InputBox.Show(DialogOwner, L.T("Bağlantı ekle"), "Adres (URL):", "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        var sel = await Js("window.getSelection().toString()");
        if (sel.Length > 0) await Js($"ms.link({JsonSerializer.Serialize(url)})");
        else await Js($"ms.insertHtml({JsonSerializer.Serialize($"<a href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(url)}</a>&nbsp;")})");
    }

    async void OnInsertTable(object sender, RoutedEventArgs e)
    {
        var size = InputBox.Show(DialogOwner, L.T("Tablo ekle"), L.T("Satır x Sütun (ör. 3x4):"), "3x3");
        if (size == null) return;
        var p = size.ToLowerInvariant().Split('x', '*', ' ');
        if (p.Length >= 2 && int.TryParse(p[0], out var r) && int.TryParse(p[^1], out var c) && r is > 0 and <= 50 && c is > 0 and <= 20)
            await Js($"ms.table({r},{c})");
    }

    string DefaultSignature() => From?.Signatures.FirstOrDefault(s=>s.Id==From.DefaultSignatureId)?.Html ?? From?.Signature ?? "";
    async void OnSignature(object sender,RoutedEventArgs e)
    {
        if(_state.IsBusy || From is not {} account)return;
        var menu=new ContextMenu();
        foreach(var signature in account.Signatures){var item=new MenuItem{Header=signature.Name};item.Click+=async(_,_)=>await Js($"ms.setSignature({JsonSerializer.Serialize(SignatureHtml(signature.Html))})");menu.Items.Add(item);}
        if(account.Signatures.Count==0&&!string.IsNullOrWhiteSpace(account.Signature)){var legacy=new MenuItem{Header=L.T("Mevcut imza")};legacy.Click+=async(_,_)=>await Js($"ms.setSignature({JsonSerializer.Serialize(SignatureHtml(account.Signature))})");menu.Items.Add(legacy);}
        var none=new MenuItem{Header=L.T("İmzasız")};none.Click+=async(_,_)=>await Js("ms.setSignature('')");menu.Items.Add(none);menu.Items.Add(new Separator());
        var manage=new MenuItem{Header=L.T("İmzaları oluştur / düzenle…")};manage.Click+=async(_,_)=>
        {
            var manager=new SignatureManagerWindow(DialogOwner,account);
            if(manager.ShowDialog()!=true)return;
            account.Signatures=manager.Signatures.ToList();account.DefaultSignatureId=manager.DefaultId;
            account.Signature=account.Signatures.FirstOrDefault(s=>s.Id==manager.DefaultId)?.Html??"";
            _saveAccounts?.Invoke();await Js($"ms.setSignature({JsonSerializer.Serialize(SignatureHtml(DefaultSignature()))})");
        };
        menu.Items.Add(manage);menu.PlacementTarget=sender as UIElement;menu.IsOpen=true;
    }

    async void OnComposeProviderChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_aiOptions==null)return;
        var name=ComposeProviderBox.SelectedItem as string;var provider=_aiOptions.Providers.FirstOrDefault(p=>p.Name==name);
        if(provider==null)return;
        try{var models=await provider.GetModelsAsync();if(ComposeProviderBox.SelectedItem as string!=name)return;ComposeModelBox.ItemsSource=models;ComposeModelBox.Text=name==_req.AiProvider?_req.AiModel??"":name==_aiOptions.Provider?_aiOptions.Model??"":models.FirstOrDefault()??"";AiProviderText.Text="AI: "+name;}
        catch(Exception ex){StatusText.Text=L.T("Model listesi alınamadı: ")+ex.Message;}
    }

    void OnSubjectChanged(object sender, TextChangedEventArgs e)
    {
        Title = string.IsNullOrWhiteSpace(SubjectBox.Text) ? L.T("İleti") : SubjectBox.Text + L.T(" - İleti");
        Changed();
    }

    void OnAttach(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("Dosya ekle") };
        if (dlg.ShowDialog(DialogOwner) == true) AddAttachmentFiles(dlg.FileNames);
    }

    void AddAttachmentFiles(IEnumerable<string> files)
    {
        if (_closed || _closingConfirmed || _state.IsBusy) return;
        var added = 0;
        var skipped = 0;
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file) || !File.Exists(file)) { skipped++; continue; }
            var fullPath = Path.GetFullPath(file);
            if (_attachments.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) continue;
            _attachments.Add(fullPath);
            added++;
        }
        StatusText.Text = skipped > 0 ? L.F("{0} ek eklendi; {1} dosyanın yolu alınamadı. Dosya Ekle düğmesini kullanabilirsiniz.", added, skipped)
            : added > 0 ? L.F("{0} ek eklendi.", added) : L.T("Bu dosyalar zaten ekli.");
    }

    void OnDragOverFiles(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = !_state.IsBusy && !_closed && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            AddAttachmentFiles(files);
        }
    }

    void OnRemoveAttach(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        if (sender is Button { Tag: string p }) _attachments.Remove(p);
    }

    void OnPickTo(object sender, RoutedEventArgs e) => Pick(ContactsWindow.Target.To);
    void OnPickCc(object sender, RoutedEventArgs e) => Pick(ContactsWindow.Target.Cc);
    void OnPickBcc(object sender, RoutedEventArgs e) => Pick(ContactsWindow.Target.Bcc);

    /// <summary>Outlook "Select Names": one dialog fills To, Cc and Bcc.</summary>
    void Pick(ContactsWindow.Target target)
    {
        var w = new ContactsWindow(pickMode: true, target) { Owner = DialogOwner };
        if (w.ShowDialog() != true) return;
        static void Append(RecipientBox box, string add)
        {
            if (add.Length == 0) return;
            var cur = box.Text.Trim().TrimEnd(';');
            box.Text = (cur.Length > 0 ? cur + "; " : "") + add + "; ";
        }
        Append(ToBox, w.PickedTo);
        Append(CcBox, w.PickedCc);
        Append(BccBox, w.PickedBcc);
    }

    // ================================================================ Send / save

    async Task<ComposeRequest> CollectAsync()
    {
        if (!_contentReady) throw new InvalidOperationException(L.T("Editör henüz hazır değil."));
        using var snapshot = JsonDocument.Parse(await Js("ms.snapshot()"));
        var r = new ComposeRequest
        {
            SubmissionId = _req.SubmissionId, SubmissionState = _req.SubmissionState,
            AiProvider=ComposeProviderBox.SelectedItem as string,AiModel=ComposeModelBox.Text.Trim(),
            DraftId = _req.DraftId, DraftAccountId = _req.DraftAccountId, DraftFolder = _req.DraftFolder,
            DraftUid = _req.DraftUid, DraftUidValidity = _req.DraftUidValidity,
            References = _req.References.ToList(), Bcc = BccBox.Text.Trim().TrimEnd(';'),
            To = ToBox.Text.Trim().TrimEnd(';'),
            Cc = CcBox.Text.Trim().TrimEnd(';'),
            Subject = SubjectBox.Text,
            FollowUpDays = FollowUpDays,
            EditableHtml=EditorContent.Sanitize(snapshot.RootElement.GetProperty("editable").GetString()),
            SignatureHtml=EditorContent.Sanitize(snapshot.RootElement.GetProperty("signature").GetString()),
            QuotedHtml=EditorContent.Sanitize(snapshot.RootElement.GetProperty("quote").GetString()),
            BodyHtml = EditorContent.Sanitize(snapshot.RootElement.GetProperty("html").GetString()),
            Body = snapshot.RootElement.GetProperty("text").GetString() ?? "",
            InReplyTo = _req.InReplyTo,
            CalendarIcs = _req.CalendarIcs, CalendarMethod = _req.CalendarMethod
        };
        r.AttachmentPaths.AddRange(_attachments);
        return r;
    }

    async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_uncertain || !_contentReady || _aiCts != null || !_state.TryBegin()) return;
        SetBusy(true);
        ComposeRequest? request = null;
        try
        {
            if (From is not { } acc) return;
            if (string.IsNullOrWhiteSpace(ToBox.Text) && string.IsNullOrWhiteSpace(CcBox.Text) && string.IsNullOrWhiteSpace(BccBox.Text))
            { StatusText.Text = L.T("En az bir alıcı girin."); return; }
            if (string.IsNullOrWhiteSpace(SubjectBox.Text) &&
                MessageBox.Show(DialogOwner, L.T("Konu satırı boş. Yine de gönderilsin mi?"), L.T("İleti"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            request = await CollectAsync();
            _drafts.Save(acc.Id, request);

            if (UndoSendAction != null && AppSettings.Shared.UndoSendSeconds > 0)
            {
                _sent = true;
                UndoSendAction(acc, request, AppSettings.Shared.UndoSendSeconds);
                _closingConfirmed = true;
                Close();
                return;
            }

            StatusText.Text = L.T("Gönderiliyor...");
            LastSendResult = await _mail.SendAsync(acc, request);
            _sent = true;
            if (LastSendResult.Warning != null)
                MessageBox.Show(DialogOwner, L.T("İleti sunucu tarafından kabul edildi. Yeniden göndermeyin.\n\n") + LastSendResult.Warning, L.T("Gönderim sonrası uyarı"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (SubmissionUncertainException ex)
        {
            _uncertain = true;
            StatusText.Text = ex.Message;
            MessageBox.Show(DialogOwner, ex.Message + L.T("\nTaslak ve gönderim kaydı yerel olarak korundu."), L.T("Gönderim sonucu belirsiz"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { StatusText.Text = L.T("Gönderilemedi: ") + ex.Message; }
        finally { if (request != null) RememberDraft(request); _state.End(); SetBusy(false); }
        if (_sent)
        {
            try { Sent?.Invoke(this, EventArgs.Empty); }
            finally { Close(); }
        }
    }

    void OnSendMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var title = new MenuItem { Header = L.T("Zamanlanmış gönder:"), IsEnabled = false, FontWeight = FontWeights.SemiBold };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());

        var tomorrow = DateTime.Today.AddDays(1).AddHours(9);
        var tomorrowItem = new MenuItem { Header = L.F("Yarın sabah ({0:dddd 09:00})", tomorrow) };
        tomorrowItem.Click += async (_, _) => await ScheduleSendAsync(tomorrow);
        menu.Items.Add(tomorrowItem);

        var daysUntilMon = ((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7;
        if (daysUntilMon == 0) daysUntilMon = 7;
        var monday = DateTime.Today.AddDays(daysUntilMon).AddHours(9);
        var mondayItem = new MenuItem { Header = L.F("Pazartesi sabah ({0:d MMM 09:00})", monday) };
        mondayItem.Click += async (_, _) => await ScheduleSendAsync(monday);
        menu.Items.Add(mondayItem);

        menu.Items.Add(new Separator());
        var customItem = new MenuItem { Header = L.T("Özel tarih ve saat…") };
        customItem.Click += async (_, _) =>
        {
            var picked = PickScheduleTime(DialogOwner, DateTime.Today.AddDays(1).AddHours(9));
            if (picked.HasValue) await ScheduleSendAsync(picked.Value);
        };
        menu.Items.Add(customItem);
        menu.IsOpen = true;
    }

    async Task ScheduleSendAsync(DateTime scheduledLocal)
    {
        if (_uncertain || !_contentReady || _aiCts != null || !_state.TryBegin()) return;
        SetBusy(true);
        ComposeRequest? request = null;
        try
        {
            if (From is not { } acc) return;
            if (string.IsNullOrWhiteSpace(ToBox.Text) && string.IsNullOrWhiteSpace(CcBox.Text) && string.IsNullOrWhiteSpace(BccBox.Text))
            { StatusText.Text = L.T("En az bir alıcı girin."); return; }
            if (string.IsNullOrWhiteSpace(SubjectBox.Text) &&
                MessageBox.Show(DialogOwner, L.T("Konu satırı boş. Yine de zamanlansın mı?"), L.T("İleti"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            request = await CollectAsync();
            _drafts.Save(acc.Id, request);

            if (ScheduleSendAction != null)
            {
                _sent = true;
                ScheduleSendAction(acc, request, new DateTimeOffset(scheduledLocal));
                _closingConfirmed = true;
                Close();
                return;
            }
            StatusText.Text = L.F("İleti zamanlandı: {0:g}", scheduledLocal);
        }
        catch (Exception ex) { StatusText.Text = L.T("Zamanlanamadı: ") + ex.Message; }
        finally { if (request != null) RememberDraft(request); _state.End(); SetBusy(false); }
    }

    public static DateTime? PickScheduleTime(Window owner, DateTime initial)
    {
        var dlg = new Window
        {
            Title = L.T("Zamanlanmış Gönder"),
            Width = 360,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = ThemeManager.Brush("BgSurfacePrimaryBrush"),
            ShowInTaskbar = false
        };
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = L.T("İletinin gönderileceği tarih ve saati seçin:"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 14), Foreground = ThemeManager.Brush("TextPrimaryBrush") });
        var dt = new Calendar.DateTimeField(initial, showTime: true) { Margin = new Thickness(0, 0, 0, 20) };
        sp.Children.Add(dt);
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = L.T("Zamanla"), Style = (Style)dlg.FindResource("PrimaryButton"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = L.T("İptal"), Style = (Style)dlg.FindResource("FlatButton"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        DateTime? result = null;
        ok.Click += (_, _) => { result = dt.Value; dlg.DialogResult = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        bp.Children.Add(ok); bp.Children.Add(cancel);
        sp.Children.Add(bp);
        dlg.Content = sp;
        Services.UiTranslator.Apply(dlg);
        return dlg.ShowDialog() == true ? result : null;
    }

    void RememberDraft(ComposeRequest r)
    {
        _req.SubmissionId = r.SubmissionId; _req.SubmissionState = r.SubmissionState;
        _req.DraftAccountId = r.DraftAccountId; _req.DraftFolder = r.DraftFolder;
        _req.DraftUid = r.DraftUid; _req.DraftUidValidity = r.DraftUidValidity;
    }

    async Task<bool> SaveDraftAsync()
    {
        if (!_editorReady || From is not { } acc) return false;
        var revision = _state.Revision;
        var request = await CollectAsync();
        _drafts.Save(acc.Id, request);
        try
        {
            var folder = await _mail.SaveDraftAsync(acc, request);
            _state.Saved(revision);
            StatusText.Text = L.F("Taslak kaydedildi ({0:HH:mm}) → {1}", DateTime.Now, folder);
            return true;
        }
        finally { RememberDraft(request); }
    }

    async void OnSaveDraft(object sender, RoutedEventArgs e)
    {
        if (_aiCts != null || !_state.TryBegin()) return;
        SetBusy(true);
        StatusText.Text = L.T("Taslak kaydediliyor...");
        try { await SaveDraftAsync(); }
        catch (Exception ex) { StatusText.Text = L.T("Sunucu taslağı kaydedilemedi; yerel kurtarma kopyasını Dosya menüsünden açabilirsiniz. ") + ex.Message; }
        finally { _state.End(); SetBusy(false); }
    }

    public Task<bool> RequestCloseAsync()
    {
        if (_closed) return Task.FromResult(true);
        if (_closeResult != null) return _closeResult.Task;
        _closeResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = _closeResult.Task;
        try { Close(); }
        catch { CompleteCloseRequest(false); throw; }
        return task;
    }

    void CompleteCloseRequest(bool closed)
    {
        _closeResult?.TrySetResult(closed);
        _closeResult = null;
    }

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_state.IsBusy || _closePrompt) { e.Cancel = true; CompleteCloseRequest(false); return; }
        if (_sent || _closingConfirmed) return;
        if (!_editorReady && _state.IsDirty) { e.Cancel = true; CompleteCloseRequest(false); StatusText.Text = L.T("Editör hazırlanıyor; değişiklikler henüz kaydedilemedi."); return; }
        _aiCts?.Cancel();
        if (!_state.IsDirty) return;
        e.Cancel = true;
        _closePrompt = true;
        var close = false;
        try
        {
            var result = _confirmClose();
            if (result == MessageBoxResult.Cancel) { CompleteCloseRequest(false); return; }
            if (result == MessageBoxResult.Yes)
            {
                if (!_state.TryBegin()) { CompleteCloseRequest(false); return; }
                SetBusy(true);
                try { close = await SaveDraftAsync(); }
                catch (Exception ex) { MessageBox.Show(DialogOwner, L.T("Sunucu kaydı tamamlanamadı. Yerel kurtarma kopyası korundu.\n") + ex.Message, L.T("Taslak")); }
                finally { _state.End(); SetBusy(false); }
            }
            else { _drafts.Delete(_req.DraftId); close = true; }
        }
        finally { _closePrompt = false; }
        if (close)
        {
            // Closing is still on the WPF call stack when No is selected. Queue a fresh close after it unwinds.
            _closingConfirmed = true;
            _ = Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); }));
        }
        else CompleteCloseRequest(false);
    }

    // ================================================================ Yapay zeka

    async Task RunAiAsync(string instruction, bool needsDraft)
    {
        if (_closed || _closingConfirmed || !_contentReady || _aiCts != null || _state.IsBusy) return;
        using var captured=JsonDocument.Parse(await Js("ms.rewriteSnapshot()"));
        var draft=captured.RootElement.GetProperty("text").GetString()??"";
        var baseline=captured.RootElement.GetProperty("baseline").GetString()??"";
        if(needsDraft&&string.IsNullOrWhiteSpace(draft)){StatusText.Text=L.T("Önce bir taslak yazın.");return;}
        var quote=needsDraft?null:await Js("document.getElementById('ms-quote').innerText");
        var prompt=DraftRewrite.Prompt(instruction,draft,quote);
        var provider=_aiOptions?.Providers.FirstOrDefault(p=>p.Name==ComposeProviderBox.SelectedItem as string);
        var model=ComposeModelBox.Text.Trim();
        if(_closed||_closingConfirmed)return;
        _aiCts = new CancellationTokenSource();
        SetBusy(true);
        BusyOverlay.Visibility = Visibility.Visible;
        Editor.Visibility = Visibility.Hidden; // hide it because WebView2 is drawn above the WPF layers
        try
        {
            string result;
            if(provider==null) result=await _ai(prompt,_aiCts.Token);
            else {var chosen=string.IsNullOrWhiteSpace(model)?null:model;result=await MailStudio.Services.Ai.AiStream.CompleteAsync(token=>provider.StreamAsync(prompt,chosen,token),_aiCts.Token);}
            if (_closed || _closingConfirmed) return;
            if(string.IsNullOrWhiteSpace(result)){StatusText.Text=L.T("Model boş yanıt verdi; taslak korunuyor.");return;}
            var preview=new RewritePreviewWindow(DialogOwner,draft,result.Trim(),needsDraft,(instruction.Contains("İngilizce",StringComparison.OrdinalIgnoreCase)||instruction.Contains("English",StringComparison.OrdinalIgnoreCase)));
            if(preview.ShowDialog()==true){var applied=await Js($"ms.applyRewrite({JsonSerializer.Serialize(result.Trim())},{JsonSerializer.Serialize(baseline)})");StatusText.Text=applied=="true"?L.T("Öneri uygulandı. İmza ve alıntı korundu."):L.T("Taslak değiştiği için öneri uygulanmadı.");}
            else StatusText.Text=L.T("Öneri uygulanmadı; mevcut metin korundu.");
        }
        catch (OperationCanceledException) { if (!_closed && !_closingConfirmed) StatusText.Text = L.T("İptal edildi."); }
        catch (Exception ex) { if (!_closed && !_closingConfirmed) StatusText.Text = L.T("AI hatası: ") + ex.Message; }
        finally
        {
            if (!_closed && !_closingConfirmed)
            {
                BusyOverlay.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
                SetBusy(false);
            }
            _aiCts?.Dispose();
            _aiCts = null;
        }
    }

    void OnCancelAi(object sender, RoutedEventArgs e) => _aiCts?.Cancel();

    async void OnAiFix(object sender, RoutedEventArgs e) => await RunAiAsync(L.T("Taslaktaki yazım, imla ve dilbilgisi hatalarını düzelt; anlamı ve dili değiştirme."), true);
    async void OnAiShorten(object sender, RoutedEventArgs e) => await RunAiAsync(L.T("Taslağı anlamını koruyarak daha kısa ve net hale getir."), true);
    async void OnAiFormal(object sender, RoutedEventArgs e) => await RunAiAsync(L.T("Taslağı profesyonel, kibar ve resmi bir iş e-postası tonuna çevir."), true);
    async void OnAiEnglish(object sender, RoutedEventArgs e) => await RunAiAsync(L.T("Taslağı profesyonel İngilizceye çevir."), true);

    async void OnAiWrite(object sender, RoutedEventArgs e)
    {
        var ins = AiInstruction.Text.Trim();
        if (ins.Length == 0) { StatusText.Text = L.T("Ne yazılacağını kısaca anlat."); AiInstruction.Focus(); return; }
        await RunAiAsync(L.T("Şu talimata göre e-postayı yaz (mevcut taslak varsa onu temel al): ") + ins, false);
    }

    void OnInstructionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnAiWrite(sender, e); e.Handled = true; }
    }
}

/// <summary>Simple single-line input window.</summary>
public static class InputBox
{
    public static string? Show(Window owner, string title, string label, string value = "")
    {
        var box = new TextBox { Text = value, Height = 24, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 10) };
        var ok = new Button { Content = L.T("Tamam"), IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = L.T("İptal"), IsCancel = true, Width = 80 };
        var w = new Window
        {
            Title = title, Owner = owner, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = new FontFamily("Segoe UI"),
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = label }, box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
                }
            }
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}

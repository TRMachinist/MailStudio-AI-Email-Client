using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Ai;
using MailStudio.Views;
using Microsoft.Web.WebView2.Core;
using MimeKit;

namespace MailStudio;

public partial class MainWindow : Window
{
    readonly AccountStore _store = new();
    readonly MailCache _cache = new();
    readonly MailService _mail;
    readonly OAuthService _oauth;
    readonly ContactBook _contacts;
    readonly Dictionary<string, InboxWatcher> _watchers = new();
    readonly HashSet<string> _alertReady = new();
    readonly AppSettings _settings = AppSettings.Shared;
    readonly List<IAiProvider> _providers;

    readonly ObservableCollection<FolderNode> _roots = new();
    readonly ObservableCollection<MailHeader> _messages = new();
    readonly ObservableCollection<ChatMessage> _chat = new();
    readonly List<ChatTurn> _history = new();
    ICollectionView _view = null!;

    FolderNode? _currentFolder;
    MailHeader? _current;
    MailContent? _currentContent;
    CancellationTokenSource? _aiCts;
    CancellationTokenSource? _loadCts;
    readonly DispatcherTimer _syncTimer = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly HashSet<Window> _hiddenOwned = new();
    readonly bool _startInTray;
    readonly TrayController? _tray;
    Task? _webInitialization;
    bool _initialized, _exitRequested, _exiting, _closed;
    public bool IsRunningInTray => _tray?.IsVisible == true && !IsVisible;
    bool _webReady;
    bool _syncing;
    string _searchQuery = "";

    public MainWindow(bool startInTray = false, bool enableTray = true)
    {
        InitializeComponent();
        UiTranslator.Apply(this);
        Title = L.T("Gelen Kutusu") + " - " + L.ProductName;
        _startInTray = startInTray;
        if (startInTray) { ShowActivated = false; ShowInTaskbar = false; WindowState = WindowState.Minimized; }
        if (enableTray) _tray = new TrayController(ShowFromTray, () => _ = SyncAllAsync(silent: false), RequestExitAsync);
        _oauth = new OAuthService(() => _settings);
        _contacts = new ContactBook(_cache);
        RecipientBox.Provider = q => _contacts.Suggest(q);
        ContactsWindow.Book = _contacts;
        ContactsWindow.NewMailTo = c => OpenCompose(new ComposeRequest { To = c.Display + "; " });
        _mail = new MailService(_cache, _oauth);
        _providers = new List<IAiProvider>
        {
            new OllamaProvider(() => _settings.OllamaUrl),
            new ClaudeCliProvider(),
            new CodexCliProvider(),
            new GeminiCliProvider(),
        };
        if (Environment.GetEnvironmentVariable("MAILSTUDIO_DEMO") == "1") _providers.Insert(0, new DemoAiProvider());

        foreach (var local in _providers.OfType<OllamaProvider>())
            local.StatusChanged += message =>
            {
                if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => { if (!_closed) AiStatus.Text = message; });
            };
        FolderTree.ItemsSource = _roots;
        var cvs = (CollectionViewSource)FindResource("MailView");
        cvs.Source = _messages;
        _view = cvs.View;
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MailHeader.DisplayGroup)));
        _view.Filter = FilterMessage;
        ChatList.ItemsSource = _chat;

        SearchBox.TextChanged += (_, _) => SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AiInput.TextChanged += (_, _) => AiPlaceholder.Visibility = AiInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TellMeBox.TextChanged += (_, _) => TellMePlaceholder.Visibility = TellMeBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _themeInit = true;
        (ThemeManager.Parse(_settings.Theme) switch { ThemeMode.Dark => ThemeDark, ThemeMode.Light => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        _themeInit = false;
        ThemeManager.ThemeChanged += OnAppThemeChanged;
        if (_settings.WindowWidth >= 600 && _settings.WindowHeight >= 400) { Width = _settings.WindowWidth; Height = _settings.WindowHeight; }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        foreach (var (label, prompt) in AiPrompts.QuickActions)
        {
            var b = new Button { Content = label, Style = (Style)FindResource("FlatButton"), Tag = prompt, FontSize = 11.5, Padding = new Thickness(7, 2, 7, 2) };
            b.Click += (_, _) => _ = RunAiAsync(prompt, label, requireMail: true);
            QuickActions.Children.Add(b);
        }

        Loaded += OnLoaded;
        PreviewKeyDown += OnWindowKey;
        Closing += OnMainClosing;
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _syncTimer.Stop(); _aiCts?.Cancel(); _loadCts?.Cancel();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear(); _mail.Dispose(); _tray?.Dispose();
            MailWeb.Dispose();
            ThemeManager.ThemeChanged -= OnAppThemeChanged;
            try { _settings.Save(); } catch { }
            if (Application.Current is App app) app.Shutdown();
        };
    }

    public void HideToTray()
    {
        if (_closed || _tray == null) return;
        foreach (var window in OwnedWindows.Cast<Window>().Where(w => w.IsVisible).ToList())
        {
            if (_hiddenOwned.Add(window)) window.Closed += OnHiddenOwnedClosed;
            window.Hide();
        }
        _oauth.AllowInteractive = false;
        Hide();
    }

    void OnHiddenOwnedClosed(object? sender, EventArgs args)
    {
        if (sender is Window window) { _hiddenOwned.Remove(window); window.Closed -= OnHiddenOwnedClosed; }
    }

    public void ShowFromTray()
    {
        if (_closed || _exitRequested) return;
        ShowActivated = true; ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _oauth.AllowInteractive = true;
        Activate(); _tray?.ClearNewMail();
        foreach (var window in _hiddenOwned.ToList())
        {
            window.Closed -= OnHiddenOwnedClosed;
            window.Show();
        }
        _hiddenOwned.Clear();
        _ = EnsureReadingWebAsync();
    }

    Task EnsureReadingWebAsync() => _webInitialization ??= InitWebAsync();
    readonly HashSet<string> _remoteImageMails=new();
    static string ImageKey(MailHeader h)=>$"{h.AccountId}/{h.Folder}/{h.UidValidity}/{h.Uid}";
    void OnLoadRemoteImages(object sender,RoutedEventArgs e)
    {
        if(_current==null||_currentContent==null)return;
        _remoteImageMails.Add(ImageKey(_current));RenderBody(_current,_currentContent);
    }

    void OnMainClosing(object? sender, CancelEventArgs args)
    {
        if (_tray != null && !_exitRequested)
        {
            args.Cancel = true; HideToTray(); return;
        }
        // Test hosts and session shutdown still protect a composer unless an explicit exit already closed it.
        var composers = OwnedWindows.OfType<ComposeWindow>().ToList();
        foreach (var composer in composers) composer.Close();
        if (composers.Any(w => !IsWindowClosed(w))) args.Cancel = true;
    }

    static bool IsWindowClosed(Window window) => !Application.Current.Windows.Cast<Window>().Contains(window);

    public async Task<bool> RequestExitAsync()
    {
        if (_closed) return true;
        if (_exiting) return false;
        _exiting = true;
        try
        {
            var composers = OwnedWindows.OfType<ComposeWindow>().ToList();
            if (composers.Count > 0) ShowFromTray();
            foreach (var composer in composers)
                if (!await composer.RequestCloseAsync()) return false;
            _exitRequested = true;
            _lifetime.Cancel(); _syncTimer.Stop(); _aiCts?.Cancel(); _loadCts?.Cancel();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            foreach (var local in _providers.OfType<OllamaProvider>())
                try { await local.StopAsync(); }
                catch (Exception ex) { Status(L.T("Model kapatma: ") + ex.Message); }
            Close();
            return _closed;
        }
        finally { _exiting = false; }
    }

    // =====================================================================
    // Startup
    // =====================================================================

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _closed) return;
        _initialized = true;
        if (_startInTray) HideToTray();
        _cache.Init();
        _store.Load();
        if (_cache.RepairedBackup != null)
            AddSystemChat(L.T("Yerel posta önbelleği bozuk bulundu (SQLite bütünlük hatası). Eski dosya silinmedi, şuraya taşındı:\n") + _cache.RepairedBackup +
                          L.T("\nİletiler sunucudan yeniden indiriliyor; hesaplar, taslaklar ve ekler etkilenmez."));
        BlockImagesCheck.IsChecked = _settings.BlockRemoteImages;
        AlertsCheck.IsChecked = _settings.DesktopAlerts;
        ConversationCheck.IsChecked = _settings.ConversationView;
        MailboxAccessCheck.IsChecked = _settings.AiMailboxAccess;
        if (_settings.ListPaneWidth >= 260) ColList.Width = new GridLength(_settings.ListPaneWidth);
        SetFolderPaneVisible(_settings.ShowFolderPane);
        SetAiPaneVisible(_settings.ShowAiPane);
        ApplyPaneThresholds();

        foreach (var p in _providers) ProviderBox.Items.Add(p.Name);
        ProviderBox.SelectedItem = _providers.Any(p => p.Name == _settings.AiProvider) ? _settings.AiProvider : "Ollama";

        if (IsVisible) await EnsureReadingWebAsync();
        await RebuildTreeAsync();
        if (_closed || _exitRequested) return;

        _syncTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.SyncIntervalMinutes));
        _syncTimer.Tick += async (_, _) => await SyncAllAsync(silent: true);
        _syncTimer.Start();

        if (_store.Accounts.Count == 0)
        {
            AddSystemChat(L.T("Hoş geldin! Başlamak için Klasör > Hesap Ekle'den bir mail hesabı bağla. ") +
                          L.T("Sağ üstten sağlayıcı seçip benimle hemen konuşabilirsin."));
            await Task.Delay(400); // let the main window render
            if (IsVisible && !_closed) OnAddAccount(this, new RoutedEventArgs());
        }
        else _ = SyncAllAsync(silent: true);
    }

    async Task InitWebAsync()
    {
        try
        {
            await MailWeb.EnsureCoreWebView2Async(await MailHtml.EnvAsync());
            if (_closed) return;
            var core = MailWeb.CoreWebView2;
            // With Windows in dark mode mails became unreadable on a black background; always a light theme like Outlook.
            core.Profile.PreferredColorScheme = ThemeManager.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, a) =>
            {
                var uri = a.Request.Uri;
                if (_settings.BlockRemoteImages && !(_current!=null&&_remoteImageMails.Contains(ImageKey(_current))) && (uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
                    a.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            core.NavigationStarting += (s, a) =>
            {
                if (a.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) || a.Uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    a.Cancel = true;
                    if (a.Uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                        OpenCompose(new ComposeRequest { To = Uri.UnescapeDataString(a.Uri[7..].Split('?')[0]) });
                    else Process.Start(new ProcessStartInfo(a.Uri) { UseShellExecute = true });
                }
            };
            core.NewWindowRequested += (s, a) =>
            {
                a.Handled = true;
                if (a.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo(a.Uri) { UseShellExecute = true });
            };
            _webReady = true;
            if (_current != null && _currentContent != null) RenderBody(_current, _currentContent);
        }
        catch (Exception ex)
        {
            Status(L.T("WebView2 başlatılamadı: ") + ex.Message);
        }
    }

    // =====================================================================
    // Folder tree
    // =====================================================================

    async Task RebuildTreeAsync()
    {
        _roots.Clear();
        NoAccountHint.Visibility = _store.Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var acc in _store.Accounts)
        {
            var root = new FolderNode { AccountId = acc.Id, Name = acc.Email, FullName = "", Icon = "", IsAccountRoot = true };
            _roots.Add(root);
            // Add the Inbox first so it can work from the cache
            root.Children.Add(new FolderNode { AccountId = acc.Id, FullName = "INBOX", Name = L.T("Gelen Kutusu"), Icon = "", SortOrder = 0,
                                               UnreadCount = _cache.UnreadCount(acc.Id, "INBOX") });
        }

        foreach (var acc in _store.Accounts.ToList())
        {
            var root = _roots.First(r => r.AccountId == acc.Id);
            try
            {
                Connection(L.F("{0} bağlanıyor...", acc.Email));
                var folders = await _mail.GetFoldersAsync(acc);
                root.Children.Clear();
                foreach (var f in folders)
                    root.Children.Add(new FolderNode { AccountId = acc.Id, FullName = f.FullName, Name = f.DisplayName, Icon = f.Icon,
                                                       SortOrder = f.Sort, UnreadCount = _cache.UnreadCount(acc.Id, f.FullName) });
                Connection(L.T("Bağlı: ") + string.Join(", ", _store.Accounts.Select(a => a.Email)));
            }
            catch (Exception ex)
            {
                Connection(L.T("Bağlantı hatası"));
                Status($"{acc.Email}: {ex.Message}");
            }
        }

        StartWatchers();
        RebuildContacts();

        if (_currentFolder == null && _roots.FirstOrDefault()?.Children.FirstOrDefault() is { } inbox)
            SelectFolder(inbox);
    }

    void SelectFolder(FolderNode node)
    {
        if (FolderTree.ItemContainerGenerator.ContainerFromItem(_roots.First(r => r.AccountId == node.AccountId)) is TreeViewItem rootItem)
        {
            rootItem.IsExpanded = true;
            rootItem.UpdateLayout();
            if (rootItem.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem child) { child.IsSelected = true; return; }
        }
        _ = OpenFolderAsync(node);
    }

    async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode n && !n.IsAccountRoot) await OpenFolderAsync(n);
    }

    async Task OpenFolderAsync(FolderNode node)
    {
        _currentFolder = node;
        _searchQuery = "";
        SearchBox.Text = "";
        Title = $"{node.Name} - {Acc(node.AccountId)?.Email} - {L.ProductName}";
        LoadMessagesFromCache();
        if (node.FullName.StartsWith("[Gmail]/", StringComparison.OrdinalIgnoreCase) && node.SortOrder == 50)
            Status(L.F("Gmail \"{0}\" bir etikettir: bu etiketi taşıyan tüm iletileri (okunmuşlar dahil) listeler. Klasördeki sayı yalnızca okunmamışları gösterir; yalnız onlar için Okunmamış filtresini kullanın.", node.Name));
        await SyncFolderAsync(node);
    }

    void LoadMessagesFromCache()
    {
        if (_currentFolder == null) return;
        var list = string.IsNullOrWhiteSpace(_searchQuery)
            ? _cache.GetHeaders(_currentFolder.AccountId, _currentFolder.FullName)
            : _cache.Search(_currentFolder.AccountId, _searchQuery);
        var selectedUid = _current?.Uid;
        FormatRuleEngine.Apply(_settings.FormatRules, list);
        _lastTotal = list.Count;
        _messages.Clear();
        foreach (var h in _settings.ConversationView && string.IsNullOrWhiteSpace(_searchQuery) ? BuildThreads(list) : list)
            _messages.Add(h);
        UpdateCounts();
        if (selectedUid != null && _messages.FirstOrDefault(m => m.Uid == selectedUid && m.Folder == _current!.Folder) is { } again)
            MessageList.SelectedItem = again;
    }

    // ---------------- Conversation view ----------------

    readonly HashSet<string> _expandedThreads = new();

    /// <summary>Groups mails with the same subject into one row (newest first); rows of expanded conversations are indented.</summary>
    List<MailHeader> BuildThreads(List<MailHeader> list)
    {
        var result = new List<MailHeader>();
        foreach (var g in ConversationGroups.Group(list).OrderByDescending(g => g.Max(h => h.Date)))
        {
            var items = g.OrderByDescending(h => h.Date).ToList();
            var head = items[0];
            head.ThreadCount = items.Count;
            head.IsThreadChild = false;
            head.GroupOverride = null;
            head.ThreadHasUnread = items.Any(h => !h.IsRead);
            head.IsThreadExpanded = items.Count > 1 && _expandedThreads.Contains(g.Key);
            result.Add(head);
            if (!head.IsThreadExpanded) continue;
            foreach (var child in items.Skip(1))
            {
                child.ThreadCount = 1;
                child.IsThreadChild = true;
                child.GroupOverride = head.GroupName; // stay in the same date group as its header
                result.Add(child);
            }
        }
        return result;
    }

    void OnThreadToggle(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MailHeader h) return;
        e.Handled = true;
        if (!_expandedThreads.Remove(h.ConversationKey)) _expandedThreads.Add(h.ConversationKey);
        LoadMessagesFromCache();
        MessageList.SelectedItem = _messages.FirstOrDefault(m => m.Uid == h.Uid && m.Folder == h.Folder);
    }

    void OnConversationToggle(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.ConversationView = ConversationCheck.IsChecked == true;
        LoadMessagesFromCache();
    }

    async Task SyncFolderAsync(FolderNode node, bool silent = false)
    {
        var acc = Acc(node.AccountId);
        if (acc == null || _lifetime.IsCancellationRequested) return;
        try
        {
            if (!silent) Status(L.F("{0} güncelleştiriliyor...", node.Name));
            var before = node.SortOrder == 0 ? _cache.GetKnownUids(acc.Id, node.FullName) : null;
            var added = await _mail.SyncFolderAsync(acc, node.FullName, _lifetime.Token, (done, total) => Dispatcher.InvokeAsync(() =>
            {
                // On a big first sync let the list fill up piece by piece
                OfflineText.Text = L.F("{0}: {1}/{2} ileti başlığı alınıyor", node.Name, done, total);
                if (_currentFolder == node && string.IsNullOrEmpty(_searchQuery) && (done <= 100 || done % 500 == 0)) LoadMessagesFromCache();
            }));
            if (before != null)
            {
                if (_alertReady.Contains(acc.Id) && _settings.DesktopAlerts && added > 0)
                {
                    var fresh = _cache.GetHeaders(acc.Id, node.FullName, 50).Where(m => !m.IsRead && !before.Contains(m.Uid)).ToList();
                    if (fresh.Count > 0) ShowDesktopAlert(node, fresh);
                }
                _alertReady.Add(acc.Id);
            }
            node.UnreadCount = _cache.UnreadCount(acc.Id, node.FullName);
            _ = PrefetchBodiesAsync(acc, node);
            if (_currentFolder == node && string.IsNullOrEmpty(_searchQuery)) LoadMessagesFromCache();
            if (!silent) Status(added > 0 ? L.F("{0} yeni öğe alındı.", added) : L.T("Tüm klasörler güncel."));
        }
        catch (Exception ex) { Status(L.F("Senkron hatası ({0}): {1}", node.Name, ex.Message)); }
    }

    readonly HashSet<string> _prefetching = new();

    /// <summary>
    /// Like Outlook's offline copy (OST): the text of all mails in the folder and the attachments (below the limit)
    /// are downloaded to the computer in the background. They open instantly on click and can be read without internet.
    /// The read state is not changed. The mail the user clicks always has priority (separate reading connection queue).
    /// </summary>
    async Task PrefetchBodiesAsync(AccountConfig acc, FolderNode node)
    {
        if (_lifetime.IsCancellationRequested) return;
        var key = acc.Id + "|" + node.FullName;
        if (!_prefetching.Add(key)) return;
        try
        {
            var missing = _cache.GetMissingBodies(acc.Id, node.FullName);
            int i = 0;
            foreach (var h in missing)
            {
                if (_lifetime.IsCancellationRequested) break;
                i++;
                if (i % 10 == 0 || i == missing.Count) OfflineProgress(L.F("Çevrimdışı kopya · {0}: {1}/{2} ileti", node.Name, i, missing.Count));
                try { await _mail.GetContentAsync(acc, h, _lifetime.Token, markRead: false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ex.Message.Contains("bulunamadı") || ex.Message.Contains("not found")) continue; // deleted on the server
                    break;                                          // connection problem: continue on the next sync
                }
            }

            if (acc.OfflineAttachments)
            {
                long limit = acc.OfflineMaxAttachmentMB * 1024L * 1024L;
                var todo = _cache.GetWithAttachments(acc.Id, node.FullName)
                    .SelectMany(x => x.atts.Where(a => a.Size <= limit).Select(a => (x.h, a)))
                    .Where(x => !File.Exists(AttachmentPath(x.h, x.a)))
                    .ToList();
                int j = 0;
                foreach (var (h, a) in todo)
                {
                    if (_lifetime.IsCancellationRequested) break;
                    j++;
                    OfflineProgress(L.F("Çevrimdışı kopya · {0}: ek {1}/{2} ({3})", node.Name, j, todo.Count, a.SizeDisplay));
                    try { await _mail.DownloadAttachmentAsync(acc, h, a, AttachmentDir(h), _lifetime.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (ex is FileNotFoundException || ex.Message.Contains("bulunamadı") || ex.Message.Contains("not found")) continue;
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _prefetching.Remove(key);
            if (_prefetching.Count == 0) OfflineProgress(OfflineSummary());
        }
    }

    void RebuildContacts()
    {
        var own = _store.Accounts.Select(a => a.Email).ToList();
        _ = Task.Run(() => { try { _contacts.Rebuild(own); } catch { } });
    }

    void OfflineProgress(string text) => Dispatcher.InvokeAsync(() => OfflineText.Text = text);

    string OfflineSummary()
    {
        int total = 0, bodies = 0;
        foreach (var f in _roots.SelectMany(r => r.Children))
        {
            var (m, b) = _cache.Stats(f.AccountId, f.FullName);
            total += m; bodies += b;
        }
        return total == 0 ? "" : L.F("Çevrimdışı: {0}/{1} ileti bilgisayarda", bodies, total);
    }

    static string AttachmentDir(MailHeader h) => AttachmentFiles.DirectoryFor(AppPaths.Attachments, h);
    static string AttachmentPath(MailHeader h, AttachmentInfo a) => AttachmentFiles.PathFor(AttachmentDir(h), a);

    async Task SyncAllAsync(bool silent)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (!silent) Status(L.T("Gönder/Al: tüm klasörler..."));
            // All folders like Outlook (Gmail's "All Mail" only when opened, because it is a copy of everything)
            foreach (var root in _roots.ToList())
                foreach (var f in root.Children.Where(c => c.SortOrder != 60).OrderBy(c => c.SortOrder).ToList())
                    await SyncFolderAsync(f, silent: true);
            if (_currentFolder != null && _currentFolder.SortOrder == 60)
                await SyncFolderAsync(_currentFolder, silent: true);
            Status(L.F("Son güncelleme: {0:HH:mm}", DateTime.Now));
            LastSyncText.Text = L.F("Son eşitleme: {0:dd.MM.yyyy HH:mm}", DateTime.Now);
            RebuildContacts();
        }
        finally { _syncing = false; }
    }

    // ---------------- Push notification ----------------

    void StartWatchers()
    {
        foreach (var acc in _store.Accounts.Where(a => !DemoData.IsDemo(a)))
        {
            if (_watchers.Remove(acc.Id, out var old)) old.Dispose();
            var w = new InboxWatcher(acc, _oauth);
            var id = acc.Id;
            w.NewMail += _ => Dispatcher.InvokeAsync(async () =>
            {
                if (_closed || _exitRequested) return;
                var inbox = _roots.FirstOrDefault(r => r.AccountId == id)?.Children.FirstOrDefault(c => c.SortOrder == 0);
                if (inbox != null) await SyncFolderAsync(inbox, silent: true);
            });
            w.StatusChanged += (_, msg) => Dispatcher.InvokeAsync(() =>
                Connection(msg.StartsWith(L.T("Anlık bildirim etkin")) ? L.T("Bağlı · anlık bildirim açık") : msg));
            _watchers[acc.Id] = w;
            w.Start();
        }
    }

    void ShowDesktopAlert(FolderNode inbox, List<MailHeader> fresh)
    {
        if (_closed || _exitRequested || fresh.Count == 0) return;
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        void OpenMail()
        {
            ShowFromTray();
            SelectFolder(inbox);
            Dispatcher.BeginInvoke(() =>
            {
                var target = _messages.FirstOrDefault(m => m.Uid == fresh[0].Uid);
                if (target != null) { MessageList.SelectedItem = target; MessageList.ScrollIntoView(target); }
            }, DispatcherPriority.Background);
        }
        if (!IsVisible && _tray != null) _tray.ShowNewMail(fresh[0], fresh.Count - 1, OpenMail);
        else new DesktopAlert(fresh[0], fresh.Count - 1, OpenMail).ShowAlert();
        FlashTitle(fresh.Count);
    }

    void FlashTitle(int count)
    {
        if (IsActive) return;
        var baseTitle = Title;
        Title = L.F("({0} yeni) {1}", count, baseTitle);
        void Restore(object? s, EventArgs e) { Title = baseTitle; Activated -= Restore; }
        Activated += Restore;
    }

    void OnAlertsChanged(object sender, RoutedEventArgs e) => _settings.DesktopAlerts = AlertsCheck.IsChecked == true;

    // =====================================================================
    // Mesaj listesi
    // =====================================================================

    bool FilterMessage(object o)
    {
        if (o is not MailHeader h) return false;
        if (FilterUnread?.IsChecked == true) return !h.IsRead;
        if (FilterFlagged?.IsChecked == true) return h.IsFlagged;
        return true;
    }

    async void OnLoadOlder(object sender, RoutedEventArgs e)
    {
        if (_currentFolder == null || Acc(_currentFolder.AccountId) is not { } acc) return;
        var oldest = _messages.Count > 0 ? _messages.Min(m => m.Date) : DateTimeOffset.Now;
        OlderLinkText.Text = L.T("Daha eski iletiler yükleniyor...");
        try
        {
            var n = await _mail.LoadOlderAsync(acc, _currentFolder.FullName, oldest, 200);
            LoadMessagesFromCache();
            Status(n > 0 ? L.F("{0} eski ileti yüklendi.", n) : L.T("Sunucuda daha eski ileti yok."));
            if (n > 0) _ = PrefetchBodiesAsync(acc, _currentFolder);
        }
        catch (Exception ex) { Status(L.T("Eski iletiler yüklenemedi: ") + ex.Message); }
        finally
        {
            OlderLinkText.Inlines.Clear();
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(L.T("Sunucudaki daha eski iletileri göster")));
            link.Click += OnLoadOlder;
            OlderLinkText.Inlines.Add(link);
        }
    }

    void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_view == null || !IsInitialized) return; // the IsChecked=True event fires early while XAML loads
        _view.Refresh();
        UpdateCounts();
    }

    int _lastTotal;

    void UpdateCounts()
    {
        // In conversation view the list holds only headers; take the counts from the real number of mails
        var unread = _currentFolder != null && string.IsNullOrEmpty(_searchQuery)
            ? _cache.UnreadCount(_currentFolder.AccountId, _currentFolder.FullName)
            : _messages.Count(m => !m.IsRead);
        CountText.Text = L.F("Öğeler: {0}    Okunmamış: {1}", Math.Max(_lastTotal, _messages.Count), unread);
        EmptyListText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (_currentFolder != null && string.IsNullOrEmpty(_searchQuery)) _currentFolder.UnreadCount = unread;
    }

    void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _searchQuery = SearchBox.Text.Trim(); LoadMessagesFromCache(); Status(_searchQuery.Length > 0 ? L.F("Arama: \"{0}\" ({1} sonuç)", _searchQuery, _messages.Count) : ""); }
        if (e.Key == Key.Escape) { SearchBox.Text = ""; _searchQuery = ""; LoadMessagesFromCache(); }
    }

    async void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItems.Count != 1 || MessageList.SelectedItem is not MailHeader h) return;
        if (_current == h && _currentContent != null) return;
        await ShowMessageAsync(h);
    }

    async Task ShowMessageAsync(MailHeader h)
    {
        if(_inlineCompose is {} draft && !await draft.RequestCloseAsync())return;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        _current = h;
        _currentContent = null;

        ReadingEmpty.Visibility = Visibility.Collapsed;
        ReadingContent.Visibility = Visibility.Visible;
        SubjectText.Text = h.SubjectDisplay;
        FromText.Text = string.IsNullOrWhiteSpace(h.FromName) ? h.FromAddress : $"{h.FromName} <{h.FromAddress}>";
        ToText.Text = L.T("Kime: ") + h.To;
        CcText.Text = "";
        DateText.Text = h.Date.ToLocalTime().ToString(L.T("dd.MM.yyyy ddd HH:mm"));
        AvatarText.Text = Initials(h.FromDisplay);
        AvatarBorder.Background = AvatarBrush(h.FromAddress);
        AttachmentList.ItemsSource = null;
        IncludeMailText.Text = L.T("Bağlam: ") + h.SubjectDisplay;

        // The attachment list was saved during sync: show it immediately
        var knownAtts = _cache.GetAttachments(h.AccountId, h.Folder, h.Uid);
        AttachmentList.ItemsSource = knownAtts.Count > 0 ? knownAtts : null;

        var cached = _cache.GetBody(h.AccountId, h.Folder, h.Uid);
        if (cached != null)
        {
            RenderBody(h, cached);
            if (h.IsRead && !cached.NeedsEncodingRefresh) return;
            if (cached.NeedsEncodingRefresh) Status(L.T("İletinin karakter kodlaması sunucudan yenileniyor…"));
        }
        else if (_webReady)
            MailWeb.NavigateToString("<html><body style='font-family:Segoe UI;color:#888;margin:20px'>" + System.Net.WebUtility.HtmlEncode(L.T("Yükleniyor…")) + "</body></html>");

        var acc = Acc(h.AccountId);
        if (acc == null) return;
        try
        {
            // Only text/HTML and embedded images are downloaded; attachments download on click (fast).
            var content = await _mail.GetContentAsync(acc, h, ct);
            if (ct.IsCancellationRequested || _current != h) return;
            if (!h.IsRead) { h.IsRead = true; Reformat(h); UpdateCounts(); }
            RenderBody(h, content);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_current == h && cached == null)
                MailWeb.NavigateToString($"<html><body style='font-family:Segoe UI;color:#a00;margin:20px'>{System.Net.WebUtility.HtmlEncode(L.T("İleti yüklenemedi: ") + ex.Message)}</body></html>");
            Status(L.T("İleti yüklenemedi: ") + ex.Message);
        }
    }

    void RenderBody(MailHeader h, MailContent c)
    {
        RemoteImagesBar.Visibility=_settings.BlockRemoteImages&&!_remoteImageMails.Contains(ImageKey(h))&&InlineImages.HasRemote(c.Html)?Visibility.Visible:Visibility.Collapsed;
        _currentContent = c;
        CcText.Text = string.IsNullOrWhiteSpace(c.Cc) ? "" : L.T("Bilgi: ") + c.Cc;
        if (c.Attachments.Count > 0) AttachmentList.ItemsSource = c.Attachments;
        if (!_webReady) return;

        MailHtml.Show(MailWeb, c.Html);
    }

    async void OnMessageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_current == null || _currentContent == null) return;
        var h = _current;
        var content = _currentContent;
        if (h.IsDraft || _roots.SelectMany(r => r.Children).Any(f => f.AccountId == h.AccountId && f.FullName == h.Folder && f.SortOrder == 1))
        {
            if (Acc(h.AccountId) is not { } acc) return;
            try { OpenCompose(await _mail.OpenDraftAsync(acc, h, AttachmentDir(h)), h.AccountId); }
            catch (Exception ex) { Status(L.T("Taslak açılamadı: ") + ex.Message); }
            return;
        }
        new MessageWindow(h, content, _settings.BlockRemoteImages,
            reply: () => Reply(h, content, all: false),
            replyAll: () => Reply(h, content, all: true),
            forward: () => ForwardAsync(h, content),
            delete: () => DeleteMessagesAsync(new[] { h }),
            openAttachment: att => OpenAttachmentAsync(h, att)) { Owner = this }.Also(FileViewers.ReturnFocusOnClose).Show();
    }

    void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { OnDelete(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter) { OnMessageDoubleClick(sender, null!); e.Handled = true; }
    }

    /// <summary>Classic Outlook keyboard shortcuts.</summary>
    void OnWindowKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (e.OriginalSource is TextBox && !ctrl && key != Key.F9) return; // do not interfere while typing
        bool h = true;
        switch (key)
        {
            case Key.N when ctrl: OnNewMail(this, e); break;
            case Key.R when ctrl && shift: OnReplyAll(this, e); break;
            case Key.R when ctrl: OnReply(this, e); break;
            case Key.F when ctrl: OnForward(this, e); break;
            case Key.Q when ctrl: SetReadSelected(true); break;
            case Key.U when ctrl: SetReadSelected(false); break;
            case Key.E when ctrl: SearchBox.Focus(); SearchBox.SelectAll(); break;
            case Key.F9: OnSyncAll(this, e); break;
            case Key.F5: OnSyncCurrent(this, e); break;
            default: h = false; break;
        }
        e.Handled = h;
    }

    async void SetReadSelected(bool read)
    {
        foreach (var m in MessageList.SelectedItems.OfType<MailHeader>().Where(x => x.IsRead != read).ToList())
        {
            if (Acc(m.AccountId) is not { } acc) continue;
            try { await _mail.SetReadAsync(acc, m, read); m.IsRead = read; Reformat(m); } catch (Exception ex) { Status(ex.Message); }
        }
        UpdateCounts();
    }

    async void OnAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AttachmentInfo att }) await OpenAttachmentAsync(att);
    }

    /// <summary>Downloads the attachment (the first time) and opens it with the default program in Windows.</summary>
    async Task OpenAttachmentAsync(AttachmentInfo att) => await OpenAttachmentAsync(_current, att);

    async Task OpenAttachmentAsync(MailHeader? h, AttachmentInfo att)
    {
        var path = await DownloadAttachmentAsync(h, att);
        if (path == null)return;
        try { FileViewers.Open(path, this, new ComposeAiOptions(_providers, CurrentProvider.Name, CurrentModel)); }
        catch (Exception ex) { Status(L.F("{0} açılamadı: {1}", att.FileName, ex.Message)); }
    }

    async Task<string?> DownloadAttachmentAsync(MailHeader? h, AttachmentInfo att)
    {
        if (h == null || Acc(h.AccountId) is not { } acc) return null;
        var dir = AttachmentDir(h);
        var local = AttachmentPath(h, att);
        if (File.Exists(local)) return local;
        try
        {
            Status(L.F("{0} indiriliyor ({1})...", att.FileName, att.SizeDisplay));
            var path = await _mail.DownloadAttachmentAsync(acc, h, att, dir);
            Status(L.F("{0} indirildi.", att.FileName));
            return path;
        }
        catch (Exception ex) { Status(L.T("Ek indirilemedi: ") + ex.Message); return null; }
    }

    async void OnAttachmentMenuOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AttachmentInfo att) await OpenAttachmentAsync(att);
    }

    void OnAttachmentSaveAs(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentInfo att) return;
        _ = SaveAttachmentAsAsync(att);
    }

    async Task SaveAttachmentAsAsync(AttachmentInfo att)
    {
        var h = _current;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = att.FileName, Title = L.T("Eki farklı kaydet") };
        if (dlg.ShowDialog(this) != true) return;
        var path = await DownloadAttachmentAsync(h, att);
        if (path != null) { File.Copy(path, dlg.FileName, true); Status(L.T("Kaydedildi: ") + dlg.FileName); }
    }

    async void OnSaveAllAttachments(object sender, RoutedEventArgs e)
    {
        if (AttachmentList.ItemsSource is not IEnumerable<AttachmentInfo> atts) return;
        var h = _current;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Eklerin kaydedileceği klasör") };
        if (dlg.ShowDialog(this) != true) return;
        var failed = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in atts.ToList())
        {
            var p = await DownloadAttachmentAsync(h, a);
            if (p == null) { failed++; continue; }
            var name = Path.GetFileName(p);
            var n = 1;
            while (!names.Add(name)) name = Path.GetFileNameWithoutExtension(p) + $" ({++n})" + Path.GetExtension(p);
            File.Copy(p, Path.Combine(dlg.FolderName, name), true);
        }
        Status(failed == 0 ? L.T("Tüm ekler kaydedildi: ") + dlg.FolderName : L.F("{0} ek kaydedilemedi; diğerleri: {1}", failed, dlg.FolderName));
    }

    // =====================================================================
    // Komutlar (ribbon)
    // =====================================================================

    void OnRibbonTabChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || RibbonHome == null) return;
        RibbonHome.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        RibbonSendRecv.Visibility = tag == "sendrecv" ? Visibility.Visible : Visibility.Collapsed;
        RibbonFolder.Visibility = tag == "folder" ? Visibility.Visible : Visibility.Collapsed;
        RibbonView.Visibility = tag == "view" ? Visibility.Visible : Visibility.Collapsed;
        RibbonAi.Visibility = tag == "ai" ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnFileMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, RoutedEventHandler h) { var mi = new MenuItem { Header = text }; mi.Click += h; menu.Items.Add(mi); }
        Add(L.T("Dosya görüntüle... (PDF, Office, STEP, DXF/DWG, kod, CNC, ZIP, e-posta)"), (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("MailStudio görüntüleyicide aç"), Filter = L.T("Tüm dosyalar|*.*|PDF|*.pdf|Office|*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.rtf|STEP|*.step;*.stp|2B CAD (DXF, DWG, DWF)|*.dxf;*.dwg;*.dwf;*.dwfx|CNC|*.nc;*.tap;*.mpf;*.h;*.iso;*.gcode|Kod / metin|*.txt;*.cs;*.py;*.json;*.xml;*.csv;*.log") };
            if (dlg.ShowDialog(this) == true)
                try { FileViewers.Open(dlg.FileName, this, new ComposeAiOptions(_providers, CurrentProvider.Name, CurrentModel)); }
                catch (Exception ex) { Status(L.T("Dosya açılamadı: ") + ex.Message); }
        });
        menu.Items.Add(new Separator());
        Add(L.T("Hesap Ekle..."), OnAddAccount);
        Add(L.T("Hesap Ayarları..."), OnEditAccount);
        Add(L.T("Hesabı Kaldır"), OnRemoveAccount);
        menu.Items.Add(new Separator());
        var drafts = new LocalDraftStore().List();
        if (drafts.Count > 0)
        {
            var recovery = new MenuItem { Header = L.T("Yerel taslakları kurtar") };
            foreach (var draft in drafts)
            {
                var item = new MenuItem { Header = $"{draft.Updated:dd.MM HH:mm} — {draft.Request.Subject}" };
                item.Click += (_, _) => OpenCompose(draft.Request, draft.AccountId);
                recovery.Items.Add(item);
            }
            menu.Items.Add(recovery);
        }
        Add(L.T("Gönderim kayıtlarını aç"), (_, _) =>
        {
            var dir = Path.Combine(AppPaths.Root, "submissions"); Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        });
        Add(L.T("Veri klasörünü aç"), (_, _) => Process.Start("explorer.exe", AppPaths.Root));
        var lang = new MenuItem { Header = "Dil / Language" };
        foreach (var (code, name) in new[] { ("tr", "Türkçe"), ("en", "English") })
        {
            var li = new MenuItem { Header = name, IsCheckable = true, IsChecked = L.Language == code };
            li.Click += (_, _) => ChangeLanguage(code);
            lang.Items.Add(li);
        }
        menu.Items.Add(lang);
        Add(L.T("Çıkış"), (_, _) => _ = RequestExitAsync());
        menu.IsOpen = true;
    }

    void OnNewMail(object sender, RoutedEventArgs e) => OpenCompose(new ComposeRequest());

    /// <summary>Saves the UI language and restarts MailStudio (the new instance waits for this one to exit).</summary>
    void ChangeLanguage(string code)
    {
        if (code == L.Language) return;
        AppSettings.Shared.Language = code; AppSettings.Shared.Save();
        var msg = code == "en" ? "MailStudio will restart in English. Continue?" : "MailStudio Türkçe olarak yeniden başlatılacak. Devam edilsin mi?";
        if (MessageBox.Show(this, msg, "MailStudio", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--restart-wait {Environment.ProcessId}") { UseShellExecute = false }); }
        catch (Exception ex) { Status(ex.Message); return; }
        _ = RequestExitAsync();
    }

    void OnNavSoon(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        if (tag == "Kişiler") { new ContactsWindow(pickMode: false) { Owner = this }.Also(FileViewers.ReturnFocusOnClose).Show(); return; }
        Status(L.F("{0} modülü sonraki aşamada eklenecek.", L.T(tag)));
    }

    bool NoSelection()
    {
        if (_current != null) return false;
        Status(L.T("Önce listeden bir ileti seçin."));
        return true;
    }

    void OnReply(object sender, RoutedEventArgs e) => Reply(all: false);
    void OnReplyAll(object sender, RoutedEventArgs e) => Reply(all: true);

    void Reply(bool all, string? body = null)
    {
        if (NoSelection() || _currentContent == null) return;
        Reply(_current!, _currentContent, all, body);
    }

    void Reply(MailHeader h, MailContent content, bool all, string? body = null)
    {
        var (to, cc) = ConversationGroups.ReplyRecipients(h, content, _store.Accounts.Select(a => a.Email), all);
        var subj = h.Subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) || h.Subject.StartsWith("YNT:", StringComparison.OrdinalIgnoreCase)
            ? h.Subject : "RE: " + h.Subject;
        OpenCompose(new ComposeRequest
        {
            To = to, Cc = cc, Subject = subj, InReplyTo = h.MessageId,
            References = content.References.Count > 0 ? content.References.ToList() : h.References.ToList(),
            Body = body ?? "", QuotedHtml = QuoteHtml(h, content)
        }, h.AccountId);
    }

    async void OnForward(object sender, RoutedEventArgs e)
    {
        if (NoSelection() || _currentContent == null) return;
        await ForwardAsync(_current!, _currentContent);
    }

    async Task<bool> ForwardAsync(MailHeader h, MailContent content)
    {
        var req = new ComposeRequest { Subject = "FW: " + h.Subject, QuotedHtml = QuoteHtml(h, content) };
        foreach (var att in content.Attachments)
        {
            var path = await DownloadAttachmentAsync(h, att);
            if (path == null)
            {
                MessageBox.Show(this, L.T("İletme hazırlanamadı. Ek indirilemedi: ") + att.FileName + L.T("\nEksik ekle devam edilmedi; yeniden deneyin."), L.T("İlet"));
                return false;
            }
            req.AttachmentPaths.Add(path);
        }
        OpenCompose(req, h.AccountId);
        return true;
    }

    static string QuoteHtml(MailHeader h, MailContent content)
    {
        string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var hdr = new StringBuilder("<div class=\"ms-hdr\">");
        hdr.Append($"<b>{E(L.T("Kimden:"))}</b> {E(h.FromDisplay)} &lt;{E(h.FromAddress)}&gt;<br>");
        hdr.Append($"<b>{E(L.T("Gönderildi:"))}</b> {E(h.Date.ToLocalTime().ToString("g", L.Culture))}<br>");
        hdr.Append($"<b>{E(L.T("Kime:"))}</b> {E(h.To)}<br>");
        if (content.Cc.Length > 0) hdr.Append($"<b>{E(L.T("Bilgi:"))}</b> {E(content.Cc)}<br>");
        hdr.Append($"<b>{E(L.T("Konu:"))}</b> {E(h.Subject)}</div>");
        return hdr + "<div>" + EditorContent.Sanitize(content.Html) + "</div>";
    }

    string QuoteBlock()
    {
        if (_current == null) return "";
        var sb = new StringBuilder();
        sb.AppendLine("________________________________");
        sb.AppendLine(L.T("Kimden: ") + FromText.Text);
        sb.AppendLine(L.F("Gönderildi: {0}", DateText.Text));
        sb.AppendLine(ToText.Text);
        if (CcText.Text.Length > 0) sb.AppendLine(CcText.Text);
        sb.AppendLine(L.T("Konu: ") + _current.Subject);
        sb.AppendLine();
        sb.Append(_currentContent?.Text ?? _current.Preview);
        return sb.ToString();
    }

    readonly HashSet<string> _openDrafts = new();
    ComposeWindow? _inlineCompose;

    void OpenCompose(ComposeRequest req, string? accountId = null, bool popOut = false)
    {
        if (_store.Accounts.Count == 0) { MessageBox.Show(L.T("Önce bir hesap ekleyin."), "MailStudio"); return; }
        var accId = accountId ?? _current?.AccountId ?? _currentFolder?.AccountId ?? _store.Accounts[0].Id;
        if (Acc(accId) == null) { Status(L.T("Taslağın hesabı bulunamadı.")); return; }
        if (!_openDrafts.Add(req.DraftId)) { Status(L.T("Bu taslak zaten açık.")); return; }
        var w = new ComposeWindow(_store.Accounts, accId, req, _mail, AiCompleteAsync, () => CurrentProvider.Name,
            aiOptions:new ComposeAiOptions(_providers,CurrentProvider.Name,CurrentModel),saveAccounts:_store.Save) { Owner = this };
        FileViewers.ReturnFocusOnClose(w);
        w.Closed += (_, _) => _openDrafts.Remove(req.DraftId);
        w.Sent += async (_, _) =>
        {
            Status(w.LastSendResult?.Warning ?? L.T("İleti sunucu tarafından kabul edildi."));
            var sent = _roots.SelectMany(r => r.Children).FirstOrDefault(f => f.AccountId == accId && f.SortOrder == 2);
            if (sent != null) await SyncFolderAsync(sent, silent: true);
        };
        w.Closed+=(_,_)=>{if(_inlineCompose==w){InlineComposeHost.Content=null;InlineComposeHost.Visibility=Visibility.Collapsed;_inlineCompose=null;if(_currentContent!=null)ReadingContent.Visibility=Visibility.Visible;}};
        w.PopOutRequested+=async(_,_)=>{try{var snapshot=await w.SnapshotAsync();var from=w.FromAccountId??accId;w.CloseForTransfer();OpenCompose(snapshot,from,popOut:true);}catch(Exception ex){Status(L.T("Taslak ayrı pencereye taşınamadı: ")+ex.Message);}};
        if(!popOut&&!string.IsNullOrEmpty(req.QuotedHtml)&&_inlineCompose==null)
        {
            _inlineCompose=w;ReadingContent.Visibility=ReadingEmpty.Visibility=Visibility.Collapsed;
            InlineComposeHost.Content=w.DetachForInline();InlineComposeHost.Visibility=Visibility.Visible;
            _ = StartInlineSafeAsync(w);
        }
        else w.Show();
    }

    async Task StartInlineSafeAsync(ComposeWindow composer){try{await composer.StartInlineAsync();}catch(Exception ex){Status(L.T("Yanıt editörü açılamadı: ")+ex.Message);}}

    async void OnDelete(object sender, RoutedEventArgs e) => await DeleteMessagesAsync(MessageList.SelectedItems.OfType<MailHeader>().ToList());

    async Task<bool> DeleteMessagesAsync(IReadOnlyCollection<MailHeader> messages)
    {
        if (messages.Count == 0) return false;
        try
        {
            var targets = new List<(MailHeader h, AccountConfig acc, bool permanent)>();
            foreach (var h in messages)
            {
                var acc = Acc(h.AccountId) ?? throw new InvalidOperationException(L.T("İletinin hesabı bulunamadı."));
                var trash = await _mail.FindSpecialAsync(acc, 3);
                if (trash == null) throw new InvalidOperationException(L.T("Silinmiş Öğeler klasörü bulunamadı; ileti silinmedi."));
                targets.Add((h, acc, trash == h.Folder));
            }
            var permanentCount = targets.Count(x => x.permanent);
            if (permanentCount > 0 && MessageBox.Show(this, L.F("Silinmiş Öğeler'deki {0} ileti kalıcı olarak silinsin mi?", permanentCount), L.T("Kalıcı sil"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            foreach (var (h, acc, permanent) in targets)
            {
                if (permanent) await _mail.DeletePermanentlyAsync(acc, h);
                else await _mail.MoveAsync(acc, h, null);
                var row = _messages.FirstOrDefault(m => m.AccountId == h.AccountId && m.Folder == h.Folder && m.UidValidity == h.UidValidity && m.Uid == h.Uid);
                if (row != null) _messages.Remove(row);
                if (_current?.AccountId == h.AccountId && _current.Folder == h.Folder && _current.Uid == h.Uid) ClearReading();
            }
            UpdateCounts();
            Status(L.F("{0} ileti silindi.", targets.Count));
            return true;
        }
        catch (Exception ex) { Status(L.T("Silinemedi: ") + ex.Message); return false; }
    }

    async void OnArchive(object sender, RoutedEventArgs e)
    {
        if (NoSelection()) return;
        var folders = _roots.SelectMany(r => r.Children).Where(f => f.AccountId == _current!.AccountId).ToList();
        // Gmail has no separate Archive folder: moving to "All Mail" removes the Inbox label (Gmail's own archive).
        var arch = folders.FirstOrDefault(f => f.SortOrder == 5) ?? folders.FirstOrDefault(f => f.SortOrder == 60);
        if (arch == null) { Status(L.T("Bu hesapta Arşiv klasörü bulunamadı.")); return; }
        await MoveSelectedAsync(arch.FullName, L.T("arşivlendi"));
    }

    void OnMoveMenu(object sender, RoutedEventArgs e)
    {
        if (NoSelection()) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var f in _roots.SelectMany(r => r.Children).Where(f => f.AccountId == _current!.AccountId && f.FullName != _current.Folder))
        {
            var mi = new MenuItem { Header = f.Name };
            var target = f.FullName;
            mi.Click += async (_, _) => await MoveSelectedAsync(target, L.T("taşındı"));
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    async Task MoveSelectedAsync(string? target, string verb)
    {
        var items = MessageList.SelectedItems.OfType<MailHeader>().ToList();
        if (items.Count == 0) return;
        var idx = MessageList.SelectedIndex;
        foreach (var h in items)
        {
            var acc = Acc(h.AccountId);
            if (acc == null) continue;
            try
            {
                await _mail.MoveAsync(acc, h, target);
                _messages.Remove(h);
            }
            catch (Exception ex) { Status(L.T("İşlem başarısız: ") + ex.Message); return; }
        }
        Status(L.F("{0} öğe {1}.", items.Count, verb));
        ClearReading();
        UpdateCounts();
        if (MessageList.Items.Count > 0) MessageList.SelectedIndex = Math.Min(idx, MessageList.Items.Count - 1);
    }

    void ClearReading()
    {
        _current = null; _currentContent = null;
        ReadingContent.Visibility = Visibility.Collapsed;
        ReadingEmpty.Visibility = Visibility.Visible;
        IncludeMailText.Text = L.T("Açık e-postayı bağlam olarak gönder");
    }

    async void OnToggleRead(object sender, RoutedEventArgs e)
    {
        foreach (var h in MessageList.SelectedItems.OfType<MailHeader>().ToList())
        {
            var acc = Acc(h.AccountId);
            if (acc == null) continue;
            try { await _mail.SetReadAsync(acc, h, !h.IsRead); h.IsRead = !h.IsRead; Reformat(h); }
            catch (Exception ex) { Status(ex.Message); }
        }
        UpdateCounts();
    }

    async void OnToggleFlag(object sender, RoutedEventArgs e)
    {
        foreach (var h in MessageList.SelectedItems.OfType<MailHeader>().ToList())
        {
            var acc = Acc(h.AccountId);
            if (acc == null) continue;
            try { await _mail.SetFlaggedAsync(acc, h, !h.IsFlagged); h.IsFlagged = !h.IsFlagged; Reformat(h); }
            catch (Exception ex) { Status(ex.Message); }
        }
    }

    async void OnMarkAllRead(object sender, RoutedEventArgs e)
    {
        var unread = _messages.Where(m => !m.IsRead).ToList();
        foreach (var h in unread)
        {
            var acc = Acc(h.AccountId);
            if (acc == null) continue;
            try { await _mail.SetReadAsync(acc, h, true); h.IsRead = true; } catch { }
        }
        UpdateCounts();
    }

    async void OnSyncAll(object sender, RoutedEventArgs e) => await SyncAllAsync(silent: false);

    async void OnSyncCurrent(object sender, RoutedEventArgs e)
    {
        if (_currentFolder != null) await SyncFolderAsync(_currentFolder);
    }

    // ---------------- Hesaplar ----------------

    async void OnAddAccount(object sender, RoutedEventArgs e)
    {
        var w = new AccountWindow(null, _settings, _oauth) { Owner = this };
        if (w.ShowDialog() == true && w.Result != null)
        {
            _store.AddOrUpdate(w.Result);
            _currentFolder = null;
            await RebuildTreeAsync();
            await SyncAllAsync(silent: false);
        }
    }

    async void OnEditAccount(object sender, RoutedEventArgs e)
    {
        var acc = Acc(_currentFolder?.AccountId ?? "") ?? _store.Accounts.FirstOrDefault();
        if (acc == null) { OnAddAccount(sender, e); return; }
        var w = new AccountWindow(acc, _settings, _oauth) { Owner = this };
        if (w.ShowDialog() == true && w.Result != null)
        {
            _store.AddOrUpdate(w.Result);
            await RebuildTreeAsync();
        }
    }

    async void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        var acc = Acc(_currentFolder?.AccountId ?? "");
        if (acc == null) return;
        if (MessageBox.Show(L.F("{0} hesabı ve yerel önbelleği kaldırılsın mı?\n(Sunucudaki maillere dokunulmaz.)", acc.Email), L.T("Hesabı Kaldır"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _oauth.SignOutAsync(acc); } catch { }
        if (_watchers.Remove(acc.Id, out var watcher)) watcher.Dispose();
        _alertReady.Remove(acc.Id);
        _store.Remove(acc.Id);
        _cache.RemoveAccount(acc.Id);
        _currentFolder = null;
        _messages.Clear();
        ClearReading();
        await RebuildTreeAsync();
    }

    // ---------------- View ----------------

    void OnToggleFolderPane(object sender, RoutedEventArgs e) => SetFolderPaneVisible(ColFolders.Width.Value == 0);

    void SetFolderPaneVisible(bool visible)
    {
        if (visible && ColFolders.Width.Value > 0 && SplitFolders.Visibility == Visibility.Visible) return;
        ColFolders.Width = visible ? new GridLength(Math.Max(160, _settings.FolderPaneWidth)) : new GridLength(0);
        SplitFolders.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _settings.ShowFolderPane = visible;
    }

    // Narrow window: first the AI pane, then the folder pane shrinks; the reading area keeps at least 360 px. The user can reopen them from the View tab.
    bool _autoCollapsedAi, _autoCollapsedFolders;
    void OnMainGridSizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) ApplyPaneThresholds(); }
    void ApplyPaneThresholds()
    {
        if (!IsLoaded) return;
        var width = MainGrid.ActualWidth; if (width <= 0) return;
        const double minReading = 360;
        double Needed() => (ColFolders.Width.Value > 0 ? ColFolders.Width.Value : 0) + ColList.Width.Value + (ColAi.Width.Value > 0 ? ColAi.Width.Value : 0) + (ColReading.Width.IsStar ? minReading : 0) + 6;
        if (Needed() > width && ColAi.Width.Value > 0 && _settings.ShowAiPane) { ColAi.Width = new GridLength(0); SplitAi.Visibility = Visibility.Collapsed; _autoCollapsedAi = true; }
        if (Needed() > width && ColFolders.Width.Value > 0 && _settings.ShowFolderPane) { ColFolders.Width = new GridLength(0); SplitFolders.Visibility = Visibility.Collapsed; _autoCollapsedFolders = true; }
        if (Needed() > width && ColList.Width.Value > 260) ColList.Width = new GridLength(Math.Max(260, ColList.Width.Value - (Needed() - width)));
        if (_autoCollapsedFolders && _settings.ShowFolderPane && Needed() + Math.Max(160, _settings.FolderPaneWidth) <= width) { _autoCollapsedFolders = false; ColFolders.Width = new GridLength(Math.Max(160, _settings.FolderPaneWidth)); SplitFolders.Visibility = Visibility.Visible; }
        if (_autoCollapsedAi && _settings.ShowAiPane && Needed() + Math.Max(260, _settings.AiPaneWidth) <= width) { _autoCollapsedAi = false; ColAi.Width = new GridLength(Math.Max(260, _settings.AiPaneWidth)); SplitAi.Visibility = Visibility.Visible; }
        if (ColFolders.Width.Value > 0) _settings.FolderPaneWidth = ColFolders.Width.Value;
        if (ColAi.Width.Value > 0) _settings.AiPaneWidth = ColAi.Width.Value;
        _settings.ListPaneWidth = ColList.Width.Value;
        if (WindowState == WindowState.Normal) { _settings.WindowWidth = ActualWidth; _settings.WindowHeight = ActualHeight; }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    bool _themeInit;
    void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_themeInit || sender is not RadioButton { Tag: string tag }) return;
        _settings.Theme = tag;
        ThemeManager.Apply(ThemeManager.Parse(tag));
        try { _settings.Save(); } catch { }
    }

    void OnAppThemeChanged()
    {
        if (_closed) return;
        if (_webReady && _current != null && _currentContent != null) RenderBody(_current, _currentContent);
        MailWeb.DefaultBackgroundColor = ThemeManager.IsDark ? System.Drawing.Color.FromArgb(0x1E, 0x29, 0x3B) : System.Drawing.Color.White;
        foreach (var m in _messages) { m.FormatBack = null; m.FormatFore = null; }
        FormatRuleEngine.Apply(_settings.FormatRules, _messages);
    }

    void Reformat(MailHeader h) => FormatRuleEngine.Apply(_settings.FormatRules, new[] { h });

    void OnFormatRules(object sender, RoutedEventArgs e)
    {
        var sample = _messages.Take(200).ToList();
        var w = new FormatRulesWindow(_settings.FormatRules, sample, (prompt, ct) =>
        {
            var provider = CurrentProvider; var model = CurrentModel;
            return AiStream.CompleteAsync(token => provider.StreamAsync(prompt, model, token), ct);
        }, () => CurrentProvider.Name) { Owner = this };
        if (w.ShowDialog() != true) return;
        _settings.FormatRules = w.Rules;
        try { _settings.Save(); } catch { }
        FormatRuleEngine.Apply(_settings.FormatRules, _messages);
        Status(L.F("Koşullu biçimlendirme: {0} etkin kural uygulandı.", _settings.FormatRules.Count(r => r.Enabled)));
    }

    void OnOpenInWindow(object sender, RoutedEventArgs e) => OnMessageDoubleClick(sender, null!);

    void OnToggleReadingPane(object sender, RoutedEventArgs e) =>
        ColReading.Width = ColReading.Width.IsStar ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

    void OnToggleAiPane(object sender, RoutedEventArgs e) => SetAiPaneVisible(ColAi.Width.Value == 0);

    void SetAiPaneVisible(bool visible)
    {
        ColAi.Width = visible ? new GridLength(Math.Max(260, _settings.AiPaneWidth)) : new GridLength(0);
        SplitAi.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _settings.ShowAiPane = visible;
        _autoCollapsedAi = false;
    }

    void OnBlockImagesChanged(object sender, RoutedEventArgs e)
    {
        _settings.BlockRemoteImages = BlockImagesCheck.IsChecked == true;
        if (_current != null && _currentContent != null) RenderBody(_current, _currentContent);
    }

    // =====================================================================
    // Yapay zeka paneli
    // =====================================================================

    IAiProvider CurrentProvider => _providers.FirstOrDefault(p => p.Name == (ProviderBox.SelectedItem as string)) ?? _providers[0];

    string? CurrentModel
    {
        get => CurrentProvider.Name switch
        {
            "Ollama" => _settings.OllamaModel,
            "Claude" => _settings.ClaudeModel,
            "Codex" => _settings.CodexModel,
            "Gemini" => _settings.GeminiModel,
            _ => null
        };
        set
        {
            switch (CurrentProvider.Name)
            {
                case "Ollama": _settings.OllamaModel = value ?? ""; break;
                case "Claude": _settings.ClaudeModel = value ?? ""; break;
                case "Codex": _settings.CodexModel = value ?? ""; break;
                case "Gemini": _settings.GeminiModel = value ?? ""; break;
            }
        }
    }

    async void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = CurrentProvider;
        _settings.AiProvider = p.Name;
        RibbonProviderText.Text = p.Name;
        ProviderBadge.Text = p.Badge;
        if (p is CliProvider cli && !cli.IsInstalled)
        {
            ProviderBadge.Text = L.F("{0} bu bilgisayarda bulunamadı.", p.Name);
            ProviderBadge.SetResourceReference(ForegroundProperty, "StatusDangerBrush");
            ProviderDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StatusDangerBrush");
        }
        else if (p is OllamaProvider)
        {
            ProviderBadge.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            ProviderDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StatusSuccessBrush");
        }
        else
        {
            ProviderBadge.Text = p is DemoAiProvider ? p.Badge : p.Badge + L.T(" · bulut");
            ProviderBadge.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            ProviderDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentPrimaryBrush");
        }

        LoginButton.Visibility = p is CliProvider { LoginArgs: not null } ? Visibility.Visible : Visibility.Collapsed;
        var models = await p.GetModelsAsync();
        ModelBox.Items.Clear();
        foreach (var m in models) ModelBox.Items.Add(string.IsNullOrEmpty(m) ? L.T("(varsayılan)") : m);
        var cur = CurrentModel;
        if (p.Name == "Codex" && string.IsNullOrEmpty(cur)) cur = CurrentModel = models.FirstOrDefault(m => m.Length > 0);
        // If a model that is no longer in the list is stored, go back to the default
        if (p.Name is "Gemini" or "Claude" && !string.IsNullOrEmpty(cur) && !models.Contains(cur)) cur = CurrentModel = "";
        if (p.Name == "Ollama" && (string.IsNullOrEmpty(cur) || !models.Contains(cur)) && models.Count > 0) cur = CurrentModel = models[0];
        ModelBox.Text = string.IsNullOrEmpty(cur) ? L.T("(varsayılan)") : cur;
        if (p.Name == "Ollama" && models.Count == 0) ProviderBadge.Text = L.T("Ollama çalışmıyor veya model yok (ollama serve).");
    }

    void OnProviderLogin(object sender, RoutedEventArgs e)
    {
        if (CurrentProvider is not CliProvider cli) return;
        try
        {
            cli.StartLogin();
            AddSystemChat(L.F("{0} için giriş penceresi açıldı. Tarayıcıda hesabınla giriş yap, bitince o siyah pencereyi kapatıp tekrar sor.", cli.Name));
        }
        catch (Exception ex) { AddSystemChat(ex.Message); }
    }

    void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is string s) CurrentModel = s == L.T("(varsayılan)") ? "" : s;
    }

    void OnModelChanged(object sender, RoutedEventArgs e)
    {
        var t = ModelBox.Text.Trim();
        CurrentModel = t == L.T("(varsayılan)") ? "" : t;
    }

    void OnMailboxAccessChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.AiMailboxAccess = MailboxAccessCheck.IsChecked == true;
        if (_settings.AiMailboxAccess)
            AddSystemChat(L.T("Posta kutusu erişimi açık: Claude/Codex/Gemini artık mailleri arayıp okuyabilir ve Taslaklar'a taslak bırakabilir. Mail göndermez. ") +
                          L.T("Örnek: \"Bu hafta tedarikçilerden gelen mailleri özetle\" veya \"Ahmet'in son teklif mailine cevap taslağı hazırla\"."));
    }

    void OnAiInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            OnAiSend(sender, e);
        }
    }

    void OnAiSend(object sender, RoutedEventArgs e)
    {
        var text = AiInput.Text.Trim();
        if (text.Length == 0) return;
        AiInput.Text = "";
        _ = RunAiAsync(text, text, requireMail: false);
    }

    void OnTellMe(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || TellMeBox.Text.Trim().Length == 0) return;
        if (ColAi.Width.Value == 0) SetAiPaneVisible(true);
        var t = TellMeBox.Text.Trim();
        TellMeBox.Text = "";
        _ = RunAiAsync(t, t, requireMail: false);
    }

    void OnAiStop(object sender, RoutedEventArgs e) => _aiCts?.Cancel();

    void OnAiClear(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        _chat.Clear();
        _history.Clear();
    }

    void OnAiSummarize(object sender, RoutedEventArgs e) => RunQuick(0);
    void OnAiDraftReply(object sender, RoutedEventArgs e) => RunQuick(1);
    void OnAiTasks(object sender, RoutedEventArgs e) => RunQuick(2);
    void OnAiTranslate(object sender, RoutedEventArgs e) => RunQuick(3);

    void RunQuick(int i)
    {
        if (ColAi.Width.Value == 0) SetAiPaneVisible(true);
        var (label, prompt) = AiPrompts.QuickActions[i];
        _ = RunAiAsync(prompt, label, requireMail: true);
    }

    async Task RunAiAsync(string prompt, string displayText, bool requireMail)
    {
        if (_aiCts != null) { Status(L.T("AI hâlâ cevap yazıyor; önce Durdur'a basın.")); return; }
        if (requireMail && _current == null) { AddSystemChat(L.T("Önce listeden bir e-posta seçin.")); return; }

        string? mailCtx = null;
        if (_current != null && (requireMail || IncludeMailCheck.IsChecked == true))
        {
            var header = _current; var content = _currentContent;
            mailCtx = await Task.Run(() => AiPrompts.MailBlock(header, content));
        }

        var provider = CurrentProvider;
        var model = CurrentModel;
        string? mcpExe = null;
        if (MailboxAccessCheck.IsChecked == true && _store.Accounts.Count > 0)
        {
            if (!provider.SupportsMcp) AddSystemChat(L.F("{0} posta kutusu araçlarını kullanamıyor; bu soru sadece açık mail bağlamıyla cevaplanacak. Erişim için Claude, Codex veya Gemini seç.", provider.Name));
            else if ((mcpExe = McpLocator.Find()) == null) AddSystemChat(L.T("MailStudio.Mcp.exe bulunamadı; posta kutusu erişimi bu soruda kapalı."));
        }
        var fullPrompt = AiPrompts.Build(_history, prompt, mailCtx, mailboxTools: mcpExe != null);

        _chat.Add(new ChatMessage { IsUser = true, Author = L.T("Sen"), Text = displayText });
        var answer = new ChatMessage { Author = provider.Name + (string.IsNullOrEmpty(model) ? "" : " · " + model), Text = "…" };
        _chat.Add(answer);
        ScrollChat();

        _aiCts = new CancellationTokenSource();
        AiSendButton.IsEnabled = false;
        AiStopButton.Visibility = Visibility.Visible;
        AiStatus.Text = L.F("{0} düşünüyor...", provider.Name);
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        try
        {
            await foreach (var chunk in AiStream.Background(token => provider.StreamAsync(fullPrompt, model, token, mcpExe), _aiCts.Token))
            {
                sb.Append(chunk);
                answer.Text = sb.ToString().TrimStart();
                ScrollChat();
                AiStatus.Text = L.F("{0} yazıyor... {1:0} sn", provider.Name, sw.Elapsed.TotalSeconds);
            }
            answer.Text = sb.ToString().Trim();
            answer.ShowActions = true;
            _history.Add(new ChatTurn("user", displayText == prompt ? prompt : $"[{displayText}] {prompt}"));
            _history.Add(new ChatTurn("assistant", answer.Text));
            AiStatus.Text = L.F("{0} · {1:0.0} sn", provider.Name, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            answer.Text = sb.Length > 0 ? sb + "\n[durduruldu]" : "[durduruldu]";
            AiStatus.Text = L.T("Durduruldu.");
        }
        catch (Exception ex)
        {
            answer.IsError = true;
            answer.Text = Hint(provider, ex.Message);
            AiStatus.Text = L.T("Hata.");
        }
        finally
        {
            _aiCts.Dispose();
            _aiCts = null;
            AiSendButton.IsEnabled = true;
            AiStopButton.Visibility = Visibility.Collapsed;
            ScrollChat();
        }
    }

    static string Hint(IAiProvider p, string msg)
    {
        var m = msg.ToLowerInvariant();
        if (p.Name == "Claude" && (m.Contains("authenticate") || m.Contains("login") || m.Contains("oauth")))
            return msg + L.T("\n\nÇözüm: Bir terminalde `claude` yazıp /login ile tekrar giriş yap.");
        if (p.Name == "Codex" && m.Contains("not supported"))
            return msg + L.T("\n\nÇözüm: Model kutusundan ChatGPT hesabının desteklediği bir model seç (ör. gpt-6-sol).");
        if (p.Name == "Gemini" && (m.Contains("auth") || m.Contains("login")))
            return msg + L.T("\n\nÇözüm: Terminalde `gemini` yazıp Google hesabınla giriş yap.");
        if (p.Name == "Ollama" && (m.Contains("connection") || m.Contains("bağlan") || m.Contains("actively refused")))
            return msg + L.T("\n\nÇözüm: Ollama uygulamasını başlat (ollama serve).");
        return msg;
    }

    /// <summary>One-shot AI call used by the draft window.</summary>
    async Task<string> AiCompleteAsync(string prompt, CancellationToken ct)
    {
        var provider = CurrentProvider; var model = CurrentModel; var full = AiPrompts.Build(Array.Empty<ChatTurn>(), prompt, null);
        return (await AiStream.CompleteAsync(token => provider.StreamAsync(full, model, token), ct)).Trim();
    }

    void OnChatCopy(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessage m }) Clipboard.SetText(m.Text);
    }

    void OnChatUseAsReply(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage m }) return;
        if (_current != null) Reply(all: false, body: m.Text);
        else OpenCompose(new ComposeRequest { Body = m.Text });
    }

    void AddSystemChat(string text)
    {
        _chat.Add(new ChatMessage { Author = "MailStudio", Text = text });
        ScrollChat();
    }

    void ScrollChat() => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

    // =====================================================================
    // Helpers
    // =====================================================================

    AccountConfig? Acc(string id) => _store.Accounts.FirstOrDefault(a => a.Id == id);

    void Status(string text) => StatusText.Text = text;
    void Connection(string text) => ConnText.Text = text;

    static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '.', '@', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return parts.Length == 1 ? parts[0][..1].ToUpperInvariant() : (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
    }

    static readonly Color[] AvatarColors =
    {
        Color.FromRgb(0x5C, 0x2D, 0x91), Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x10, 0x7C, 0x10),
        Color.FromRgb(0xC2, 0x39, 0xB3), Color.FromRgb(0xCA, 0x50, 0x10), Color.FromRgb(0x03, 0x83, 0x87),
        Color.FromRgb(0x8E, 0x56, 0x2E), Color.FromRgb(0xA4, 0x26, 0x2C),
    };

    static Brush AvatarBrush(string key)
    {
        int h = 0;
        foreach (var ch in key) h = h * 31 + ch;
        return new SolidColorBrush(AvatarColors[Math.Abs(h % AvatarColors.Length)]);
    }
}

/// <summary>Glyph and color for an attachment chip by file type.</summary>
public sealed class AttachmentIcon : IValueConverter
{
    public static readonly AttachmentIcon Glyph = new(0), Brush = new(1), NotEmpty = new(2);
    readonly int _mode;
    AttachmentIcon(int mode) => _mode = mode;
    static string Ext(object? v) => Path.GetExtension(v as string ?? "").TrimStart('.').ToLowerInvariant();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (_mode == 2) return !string.IsNullOrEmpty(value as string);
        var ext = Ext(value);
        var (glyph, key) = ext switch
        {
            "stp" or "step" or "igs" or "iges" or "x_t" or "sldprt" or "prt" => ("\uE7B3", "AccentPrimaryBrush"),
            "pdf" => ("\uE8A5", "StatusDangerBrush"),
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "tif" or "tiff" => ("\uEB9F", "IconBrush"),
            "xlsx" or "xls" or "csv" => ("\uE9F9", "StatusSuccessBrush"),
            "docx" or "doc" => ("\uE8A5", "IconBrush"),
            "zip" or "rar" or "7z" => ("\uE8B7", "IconBrush"),
            "nc" or "mpf" or "h" or "eia" or "txt" => ("\uE943", "IconBrush"),
            _ => ("\uE723", "IconBrush"),
        };
        return _mode == 0 ? glyph : ThemeManager.Brush(key);
    }
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → brush; empty value is transparent.</summary>
public sealed class HexBrush : IValueConverter
{
    public static readonly HexBrush Instance = new();
    static readonly Dictionary<string, Brush> Cache = new();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0) return Brushes.Transparent;
        // In the dark theme light backgrounds are pulled to dark tones and dark text colors to light tones; readability is kept.
        var key = (ThemeManager.IsDark ? "d" : "l") + (parameter as string) + hex;
        if (Cache.TryGetValue(key, out var b)) return b;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            if (ThemeManager.IsDark)
            {
                var basis = Color.FromRgb(0x29, 0x29, 0x29);
                c = parameter as string == "back" ? Mix(basis, c, .28) : Mix(c, Colors.White, .35);
            }
            b = new SolidColorBrush(c); b.Freeze();
        }
        catch { b = Brushes.Transparent; }
        return Cache[key] = b;
    }
    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

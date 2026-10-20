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
using MailStudio.Views.Calendar;
using MailStudio.Views.Quotes;
using MailStudio.Views.QuickSteps;
using MailStudio.Views.Snooze;
using MailStudio.Views.Templates;
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
    readonly RuleStore _ruleStore = new();
    readonly FollowUpStore _followUpStore = new();
    public FollowUpStore FollowUpStore => _followUpStore;
    readonly OutboxStore _outbox = new();
    public OutboxStore OutboxStore => _outbox;
    readonly TemplateStore _templateStore = new();
    public TemplateStore TemplateStore => _templateStore;
    readonly QuickStepStore _quickStepStore = new();
    public QuickStepStore QuickStepStore => _quickStepStore;
    readonly SnoozeStore _snoozeStore = new();
    public SnoozeStore SnoozeStore => _snoozeStore;
    readonly SearchFolderStore _searchFolderStore = new();
    public SearchFolderStore SearchFolderStore => _searchFolderStore;
    readonly DispatcherTimer _snoozeTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _queueTimer = new() { Interval = TimeSpan.FromSeconds(1) };
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
        NoAccountRun.Text = L.T("Henüz hesap yok."); AddAccountRun.Text = L.T("Hesap ekle");
        UiTranslator.Apply(this);
        Title = L.T("Gelen Kutusu") + " - " + L.ProductName;
        _startInTray = startInTray;
        if (startInTray) { ShowActivated = false; ShowInTaskbar = false; WindowState = WindowState.Minimized; }
        if (enableTray) _tray = new TrayController(ShowFromTray, () => _ = SyncAllAsync(silent: false), RequestExitAsync);
        _oauth = new OAuthService(() => _settings);
        _contacts = new ContactBook(_cache, new ContactStore());
        RecipientBox.Provider = q => _contacts.Suggest(q);
        ContactsWindow.Book = _contacts;
        AddressChips.Store = _contacts.Store;
        AddressChips.NewMailTo = to => OpenCompose(new ComposeRequest { To = to });
        AddressChips.FindMailsOf = FindMailsOf;
        AddressChips.ShowContactCard = (name, addr) => OpenContactCard(name, addr);
        ContactsWindow.FindMailsOf = FindMailsOf;
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
        _queueTimer.Tick += (_, _) => QueueTick();
        _snoozeTimer.Tick += (_, _) => CheckSnoozeDue();
        _snoozeTimer.Start();
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
            _closed = true; _lifetime.Cancel(); _syncTimer.Stop(); _snoozeTimer.Stop(); _aiCts?.Cancel(); _loadCts?.Cancel();
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
        // A mail still in its undo-send window is never dropped silently
        if (!_outboxConfirmedAtExit && !_exitRequested && HasPendingUndo)
        {
            args.Cancel = true;
            Dispatcher.BeginInvoke(async () => { if (await ConfirmPendingOutboxAsync()) Close(); });
            return;
        }
        // Test hosts and session shutdown still protect a composer unless an explicit exit already closed it.
        var composers = OwnedWindows.OfType<ComposeWindow>().ToList();
        foreach (var composer in composers) composer.Close();
        if (composers.Any(w => !IsWindowClosed(w))) args.Cancel = true;
        else ClosePim();
    }

    bool _outboxConfirmedAtExit;
    bool HasPendingUndo => !App.SelfTesting && _outbox.Items.Any(i => i.IsUndoSend && i.Status == OutboxStatus.Scheduled);

    /// <summary>On exit with a mail in its undo window: send it now, keep it in the outbox (asked at next start) or cancel the exit.</summary>
    async Task<bool> ConfirmPendingOutboxAsync()
    {
        if (_outboxConfirmedAtExit || !HasPendingUndo) return true;
        var answer = MessageBox.Show(this, L.T("Gönderilmeyi bekleyen ileti var (geri alma süresi dolmadı).\n\nEvet: şimdi gönder ve kapat\nHayır: Giden Kutusu'nda kalsın, bir sonraki açılışta sorulsun\nİptal: kapatma"),
            "MailStudio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes)
        {
            foreach (var i in _outbox.Items.Where(i => i.IsUndoSend && i.Status == OutboxStatus.Scheduled)) _outbox.Release(i.Id);
            for (var n = 0; n < 400 && (_outbox.GetPendingDue().Count > 0 || _queueBusy); n++) { QueueTick(); await Task.Delay(250); }
        }
        _outboxConfirmedAtExit = true;
        return true;
    }

    static bool IsWindowClosed(Window window) => !Application.Current.Windows.Cast<Window>().Contains(window);

    public async Task<bool> RequestExitAsync()
    {
        if (_closed) return true;
        if (_exiting) return false;
        _exiting = true;
        try
        {
            if (HasPendingUndo) { ShowFromTray(); if (!await ConfirmPendingOutboxAsync()) return false; }
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
        InitPim();
        _cache.Corrupted += () => Dispatcher.BeginInvoke(() => AddSystemChat(L.T("Yerel posta önbelleğinde bozuk bir bölüm bulundu. İletiler şimdilik sunucudan açılıyor; MailStudio'yu kapatıp açtığınızda önbellek kendiliğinden onarılır (hesaplar, taslaklar ve ekler etkilenmez).")));
        _store.Load();
        _queueTimer.Start();   // after the accounts are loaded
        RevisionLookup.Resolver = PickOlderRevisionAsync;
        RevisionLookup.ShowDiff = (oldPath, newPath) =>
            ShowRevisionDiffAsync(oldPath, newPath, _attachmentOrigins.TryGetValue(Path.GetFullPath(newPath), out var o) ? o.Header : null);
        _outbox.Changed += () => Dispatcher.BeginInvoke(UpdateUndoBar);
        Dispatcher.BeginInvoke(AskHeldOutbox, DispatcherPriority.ApplicationIdle);
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
        PopulateQuickStepButtons();

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
        if (_store.Accounts.Count > 0)
        {
            var unifiedNode = new FolderNode
            {
                AccountId = "",
                FullName = "UNIFIED_INBOX",
                Name = L.T("Tüm Gelen Kutuları"),
                Icon = "\uE715",
                IsUnifiedInbox = true,
                SortOrder = -100,
                CustomColor = AccountColorHelper.GetAccountColor(_store.Accounts[0], 0),
                UnreadCount = _cache.GetUnifiedInboxUnreadCount()
            };
            _roots.Add(unifiedNode);

            int accIdx = 0;
            foreach (var acc in _store.Accounts)
            {
                var color = AccountColorHelper.GetAccountColor(acc, accIdx++);
                var root = new FolderNode
                {
                    AccountId = acc.Id,
                    Name = acc.Email,
                    FullName = "",
                    Icon = "\uE77B",
                    IsAccountRoot = true,
                    CustomColor = color
                };
                _roots.Add(root);
                root.Children.Add(new FolderNode
                {
                    AccountId = acc.Id,
                    FullName = "INBOX",
                    Name = L.T("Gelen Kutusu"),
                    Icon = "\uE80C",
                    SortOrder = 0,
                    CustomColor = color,
                    UnreadCount = _cache.UnreadCount(acc.Id, "INBOX")
                });
            }

            var searchRoot = new FolderNode
            {
                AccountId = "",
                FullName = "SEARCH_FOLDERS_ROOT",
                Name = L.T("Arama Klasörleri"),
                Icon = "\uE721",
                IsSearchFolderRoot = true,
                IsExpanded = true,
                SortOrder = 1000
            };
            foreach (var sf in _searchFolderStore.Folders)
            {
                searchRoot.Children.Add(new FolderNode
                {
                    AccountId = "",
                    FullName = $"SEARCH_{sf.Id}",
                    Name = L.T(sf.Name),
                    Icon = sf.Icon,
                    IsSearchFolder = true,
                    SearchFolderId = sf.Id,
                    UnreadCount = _searchFolderStore.CountMatching(sf, _cache, unreadOnly: true)
                });
            }
            _roots.Add(searchRoot);
        }

        foreach (var acc in _store.Accounts.ToList())
        {
            var root = _roots.FirstOrDefault(r => r.IsAccountRoot && r.AccountId == acc.Id);
            if (root == null) continue;
            try
            {
                Connection(L.F("{0} bağlanıyor...", acc.Email));
                var folders = await _mail.GetFoldersAsync(acc);
                root.Children.Clear();
                var color = AccountColorHelper.GetAccountColor(acc, _store.Accounts.IndexOf(acc));
                foreach (var f in folders)
                    root.Children.Add(new FolderNode
                    {
                        AccountId = acc.Id,
                        FullName = f.FullName,
                        Name = f.DisplayName,
                        Icon = f.Icon,
                        CustomColor = f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? color : null,
                        SortOrder = f.Sort,
                        UnreadCount = _cache.UnreadCount(acc.Id, f.FullName)
                    });
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

        if (_currentFolder == null)
        {
            var defaultNode = _roots.FirstOrDefault(r => r.IsUnifiedInbox)
                           ?? _roots.FirstOrDefault(r => r.IsAccountRoot)?.Children.FirstOrDefault();
            if (defaultNode != null)
                SelectFolder(defaultNode);
        }
    }

    void SelectFolder(FolderNode node)
    {
        var root = _roots.FirstOrDefault(r => r == node || r.Children.Contains(node) || (r.IsAccountRoot && r.AccountId == node.AccountId));
        if (root != null && FolderTree.ItemContainerGenerator.ContainerFromItem(root) is TreeViewItem rootItem)
        {
            rootItem.IsExpanded = true;
            rootItem.UpdateLayout();
            if (root == node) { rootItem.IsSelected = true; return; }
            if (rootItem.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem child) { child.IsSelected = true; return; }
        }
        _ = OpenFolderAsync(node);
    }

    async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode n && !n.IsAccountRoot && !n.IsSearchFolderRoot) await OpenFolderAsync(n);
    }

    async Task OpenFolderAsync(FolderNode node)
    {
        _currentFolder = node;
        _searchQuery = "";
        SearchBox.Text = "";
        string folderTitle;
        if (node.IsUnifiedInbox)
            folderTitle = $"{node.Name} - {L.ProductName}";
        else if (node.IsSearchFolder)
            folderTitle = $"{node.Name} - {L.T("Arama Klasörü")} - {L.ProductName}";
        else
            folderTitle = $"{node.Name} - {Acc(node.AccountId)?.Email} - {L.ProductName}";

        if (_module == PimModule.Mail) Title = folderTitle; else _mailTitle = folderTitle;
        LoadMessagesFromCache();
        if (node.FullName.StartsWith("[Gmail]/", StringComparison.OrdinalIgnoreCase) && node.SortOrder == 50)
            Status(L.F("Gmail \"{0}\" bir etikettir: bu etiketi taşıyan tüm iletileri (okunmuşlar dahil) listeler. Klasördeki sayı yalnızca okunmamışları gösterir; yalnız onlar için Okunmamış filtresini kullanın.", node.Name));
        await SyncFolderAsync(node);
    }

    void EnrichWithAccountInfo(List<MailHeader> list)
    {
        var accList = _store.Accounts.ToList();
        var accDict = accList.ToDictionary(a => a.Id, a => a);
        for (int i = 0; i < list.Count; i++)
        {
            var h = list[i];
            if (accDict.TryGetValue(h.AccountId, out var acc))
            {
                var idx = accList.IndexOf(acc);
                h.AccountName = !string.IsNullOrEmpty(acc.DisplayName) ? acc.DisplayName : acc.Email;
                h.AccountColor = AccountColorHelper.GetAccountColor(acc, idx);
                h.ShowAccountBadge = true;
            }
        }
    }

    void LoadMessagesFromCache()
    {
        if (_currentFolder == null) return;
        List<MailHeader> list;
        if (_currentFolder.IsUnifiedInbox)
        {
            list = string.IsNullOrWhiteSpace(_searchQuery)
                ? _cache.GetUnifiedInboxHeaders()
                : _cache.Search(null, _searchQuery);
            EnrichWithAccountInfo(list);
        }
        else if (_currentFolder.IsSearchFolder && _currentFolder.SearchFolderId != null)
        {
            var sf = _searchFolderStore.Find(_currentFolder.SearchFolderId);
            list = sf != null ? _searchFolderStore.ExecuteSearch(sf, _cache) : new List<MailHeader>();
            EnrichWithAccountInfo(list);
        }
        else
        {
            list = string.IsNullOrWhiteSpace(_searchQuery)
                ? _cache.GetHeaders(_currentFolder.AccountId, _currentFolder.FullName)
                : _cache.Search(_currentFolder.AccountId, _searchQuery);
        }

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
        if (node.IsUnifiedInbox)
        {
            foreach (var a in _store.Accounts)
            {
                var inbox = _roots.FirstOrDefault(r => r.IsAccountRoot && r.AccountId == a.Id)?.Children.FirstOrDefault(c => c.SortOrder == 0);
                if (inbox != null) await SyncFolderAsync(inbox, silent: true);
            }
            LoadMessagesFromCache();
            return;
        }
        if (node.IsSearchFolder || node.IsSearchFolderRoot) return;

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
                if (_alertReady.Contains(acc.Id) && added > 0)
                {
                    var newMail = _cache.GetHeaders(acc.Id, node.FullName, 50).Where(m => !before.Contains(m.Uid)).ToList();
                    var fresh = newMail.Where(m => !m.IsRead).ToList();
                    if (_settings.DesktopAlerts && fresh.Count > 0)
                    {
                        ShowDesktopAlert(node, fresh);
                        _ = AutoRunSkillsAsync(fresh);
                    }
                    // Mail rules run on every new Inbox message (independent of desktop alerts)
                    if (newMail.Count > 0) _ = RunRulesOnFreshAsync(acc, newMail);
                }
                if (added > 0)
                {
                    var freshAll = _cache.GetHeaders(acc.Id, node.FullName, 50).Where(m => before == null || !before.Contains(m.Uid)).ToList();
                    if (freshAll.Count > 0 && _followUpStore.CheckReplies(freshAll, _tasks) > 0)
                    {
                        _tasksView?.Refresh();
                    }
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
        foreach (var f in _roots.Where(r => r.IsAccountRoot).SelectMany(r => r.Children))
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
            foreach (var root in _roots.Where(r => r.IsAccountRoot).ToList())
                foreach (var f in root.Children.Where(c => c.SortOrder != 60).OrderBy(c => c.SortOrder).ToList())
                    await SyncFolderAsync(f, silent: true);
            if (_currentFolder != null && _currentFolder.SortOrder == 60)
                await SyncFolderAsync(_currentFolder, silent: true);
            if (_roots.FirstOrDefault(r => r.IsUnifiedInbox) is { } uInbox)
                uInbox.UnreadCount = _cache.GetUnifiedInboxUnreadCount();
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
        bool isSnoozed = _snoozeStore.IsSnoozed(h.AccountId, h.Folder, h.Uid);
        if (FilterSnoozed?.IsChecked == true) return isSnoozed;
        if (isSnoozed) return false;
        if (FilterNewsletters?.IsChecked == true) return NewsletterHelper.IsNewsletter(h, _ruleStore);
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
        int unread;
        if (_currentFolder == null)
            unread = 0;
        else if (_currentFolder.IsUnifiedInbox)
            unread = _cache.GetUnifiedInboxUnreadCount();
        else if (_currentFolder.IsSearchFolder)
            unread = _messages.Count(m => !m.IsRead);
        else if (string.IsNullOrEmpty(_searchQuery))
            unread = _cache.UnreadCount(_currentFolder.AccountId, _currentFolder.FullName);
        else
            unread = _messages.Count(m => !m.IsRead);

        CountText.Text = L.F("Öğeler: {0}    Okunmamış: {1}", Math.Max(_lastTotal, _messages.Count), unread);
        EmptyListText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (_currentFolder != null && string.IsNullOrEmpty(_searchQuery)) _currentFolder.UnreadCount = unread;
    }

    /// <summary>Searches the current mailbox for a person's address (from the address links and the address book).</summary>
    void FindMailsOf(string address)
    {
        Activate();
        SearchBox.Text = address;
        _searchQuery = address;
        LoadMessagesFromCache();
        Status(L.F("Arama: \"{0}\" ({1} sonuç)", _searchQuery, _messages.Count));
    }

    public void OpenContactCard(string name, string address)
    {
        var win = new ContactCardWindow(name, address, _cache, _tasks, _quoteStore, _contacts.Store) { Owner = this };
        win.ShowDialog();
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
        AddressChips.Fill(FromPanel, null, string.IsNullOrWhiteSpace(h.FromName) ? h.FromAddress : $"\"{h.FromName}\" <{h.FromAddress}>", 13.5, bold: true, ThemeManager.Brush("TextPrimaryBrush"));
        AddressChips.Fill(ToPanel, L.T("Kime: "), h.To);
        AddressChips.Fill(CcPanel, null, null);
        DateText.Text = h.Date.ToLocalTime().ToString(L.T("dd.MM.yyyy ddd HH:mm"));
        AvatarText.Text = Initials(h.FromDisplay);
        AvatarBorder.Background = AvatarBrush(h.FromAddress);
        AttachmentList.ItemsSource = null;
        InviteBar.Visibility = Visibility.Collapsed;
        UnsubscribeBar.Visibility = Visibility.Collapsed;
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
        try { OnSkillContext(h, c); } catch { }
        CcText.Text = string.IsNullOrWhiteSpace(c.Cc) ? "" : L.T("Bilgi: ") + c.Cc;
        AddressChips.Fill(CcPanel, L.T("Bilgi: "), c.Cc);
        try { ShowInvitation(h, c); } catch (Exception ex) { InviteBar.Visibility = Visibility.Collapsed; Status(L.T("Davet okunamadı: ") + ex.Message); }
        try { UpdateUnsubscribeBar(h, c); } catch { UnsubscribeBar.Visibility = Visibility.Collapsed; }
        if (c.Attachments.Count > 0) AttachmentList.ItemsSource = c.Attachments;
        if (!_webReady) return;

        MailHtml.Show(MailWeb, c.Html);
    }

    void UpdateUnsubscribeBar(MailHeader h, MailContent c)
    {
        var unsubHeader = !string.IsNullOrEmpty(c.ListUnsubscribe) ? c.ListUnsubscribe : h.ListUnsubscribe;
        var unsubPost = !string.IsNullOrEmpty(c.ListUnsubscribePost) ? c.ListUnsubscribePost : h.ListUnsubscribePost;
        var info = NewsletterHelper.ParseUnsubscribe(unsubHeader, unsubPost);
        bool isNewsletter = info != null || NewsletterHelper.IsNewsletter(h, _ruleStore, c);

        if (isNewsletter)
        {
            UnsubscribeBar.Visibility = Visibility.Visible;
            UnsubscribeButton.Visibility = info != null ? Visibility.Visible : Visibility.Collapsed;
            UnsubscribeButton.Tag = info;
            UnsubscribeBarText.Text = info != null
                ? L.T("Bu ileti bir bülten veya toplu listedir.")
                : L.T("Bu ileti bir bülten olarak algılandı.");
        }
        else
        {
            UnsubscribeBar.Visibility = Visibility.Collapsed;
        }
    }

    async void OnUnsubscribeClick(object sender, RoutedEventArgs e)
    {
        var h = _current;
        if (h == null) return;
        var info = (sender as FrameworkElement)?.Tag as UnsubscribeInfo
                   ?? NewsletterHelper.ParseUnsubscribe(_currentContent?.ListUnsubscribe ?? h.ListUnsubscribe,
                                                        _currentContent?.ListUnsubscribePost ?? h.ListUnsubscribePost);

        if (info == null)
        {
            Status(L.T("Abonelikten çıkma bağlantısı bulunamadı."));
            return;
        }

        if (!string.IsNullOrEmpty(info.HttpUri))
        {
            var msg = L.F("Abonelikten çıkmak için web sitesi açılsın mı?\n\n{0}", info.HttpUri);
            var title = L.T("Abonelikten Çık");
            var res = MessageBox.Show(this, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = info.HttpUri, UseShellExecute = true });
                    Status(L.T("Web sitesi tarayıcıda açıldı."));
                }
                catch (Exception ex)
                {
                    Status(L.T("Bağlantı açılamadı: ") + ex.Message);
                }
                return;
            }
            else if (string.IsNullOrEmpty(info.MailtoUri))
            {
                return;
            }
        }

        if (!string.IsNullOrEmpty(info.MailtoUri))
        {
            OpenUnsubscribeMailDraft(info.MailtoUri);
        }
    }

    public void OpenUnsubscribeMailDraft(string mailtoUri)
    {
        var (to, subject, body) = NewsletterHelper.ParseMailto(mailtoUri);
        var acc = Acc(_current?.AccountId ?? "") ?? _store.Accounts.FirstOrDefault();
        if (acc == null) return;

        var req = new ComposeRequest
        {
            To = to,
            Subject = string.IsNullOrWhiteSpace(subject) ? "Unsubscribe" : subject,
            BodyHtml = string.IsNullOrWhiteSpace(body) ? "<p>Unsubscribe</p>" : $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>",
            Body = string.IsNullOrWhiteSpace(body) ? "Unsubscribe" : body
        };
        OpenCompose(req, acc.Id);
        Status(L.T("Abonelikten çıkma e-postası taslak olarak açıldı."));
    }

    async void OnAlwaysMoveToNewslettersClick(object sender, RoutedEventArgs e)
    {
        var h = _current ?? MessageList.SelectedItem as MailHeader;
        if (h == null) return;
        await AlwaysMoveSenderToNewslettersAsync(h);
    }

    public async Task AlwaysMoveSenderToNewslettersAsync(MailHeader h)
    {
        var senderAddr = !string.IsNullOrEmpty(h.FromAddress) ? h.FromAddress : h.FromName;
        if (string.IsNullOrWhiteSpace(senderAddr)) return;

        // an existing "Bültenler"/"Newsletters" folder is reused whatever the interface language
        var existingFolder = _roots.SelectMany(r => r.Children).FirstOrDefault(f => f.AccountId == h.AccountId &&
            (f.Name.Equals("Bültenler", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("Newsletters", StringComparison.OrdinalIgnoreCase)));
        var folderName = existingFolder?.FullName ?? (L.IsEnglish ? "Newsletters" : "Bültenler");
        var acc = Acc(h.AccountId);
        if (acc == null) return;
        try { folderName = await _mail.EnsureFolderAsync(acc, folderName); }
        catch (Exception ex) { Status(L.T("Bülten klasörü oluşturulamadı: ") + ex.Message); return; }

        // one rule per sender (also when it was made in the other language)
        _ruleStore.ReloadIfChanged();
        var existing = _ruleStore.Rules.FirstOrDefault(r =>
            r.Conditions.Any(c => c.Field == "from" && c.Value.Equals(senderAddr, StringComparison.OrdinalIgnoreCase)) &&
            r.Actions.Any(a => a.Type == "move_to_folder" && a.Value != null &&
                               (a.Value.Equals(folderName, StringComparison.OrdinalIgnoreCase) ||
                                a.Value.EndsWith("Newsletters", StringComparison.OrdinalIgnoreCase) ||
                                a.Value.EndsWith("Bültenler", StringComparison.OrdinalIgnoreCase))));
        if (existing == null)
        {
            _ruleStore.Rules.Add(NewsletterHelper.CreateMoveToNewslettersRule(h, folderName));
            try { _ruleStore.Save(); } catch (IOException ex) { Status(L.T("Kurallar kaydedilemedi: ") + ex.Message); }
        }

        var matching = _messages.Where(m => m.AccountId == h.AccountId && m.FromAddress.Equals(h.FromAddress, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!matching.Contains(h)) matching.Add(h);
        int moved = 0, failed = 0;
        foreach (var m in matching)
        {
            try { await _mail.MoveAsync(acc, m, folderName); moved++; } catch (Exception) { failed++; }
        }
        LoadMessagesFromCache();
        Status(failed == 0
            ? L.F("'{0}' göndereni için kural oluşturuldu; {1} ileti \"{2}\" klasörüne taşındı.", h.FromDisplay, moved, folderName)
            : L.F("'{0}' göndereni için kural oluşturuldu; {1} ileti taşındı, {2} ileti taşınamadı.", h.FromDisplay, moved, failed));
        if (existingFolder == null) { try { await RebuildTreeAsync(); } catch (Exception ex) { Status(ex.Message); } }
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
        if (ctrl && !shift && key is >= Key.D1 and <= Key.D5) { SwitchModule((PimModule)(key - Key.D1)); e.Handled = true; return; }
        if (ctrl && shift && key is >= Key.D1 and <= Key.D9)
        {
            var shortcutStr = $"Ctrl+Shift+{(int)(key - Key.D0)}";
            var step = _quickStepStore.FindByShortcut(shortcutStr);
            if (step != null && _module == PimModule.Mail) { _ = ExecuteQuickStepAsync(step); e.Handled = true; return; }
        }
        if (_module != PimModule.Mail) { if (key == Key.F9) { OnSyncAll(this, e); e.Handled = true; } return; } // the calendar / task view handles its own keys
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
            case Key.B when ctrl && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): OnAddressBook(this, new RoutedEventArgs()); break;
            case Key.K when ctrl && shift: OnMailToTask(this, e); break;
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
        if (File.Exists(local)) { _attachmentOrigins[Path.GetFullPath(local)] = (h, att); return local; }
        try
        {
            Status(L.F("{0} indiriliyor ({1})...", att.FileName, att.SizeDisplay));
            var path = await _mail.DownloadAttachmentAsync(acc, h, att, dir);
            Status(L.F("{0} indirildi.", att.FileName));
            _attachmentOrigins[Path.GetFullPath(path)] = (h, att);
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

    async void OnAttachmentCompareRevision(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentInfo att) return;
        await CompareAttachmentRevisionAsync(att);
    }

    /// <summary>Which mail a downloaded attachment file belongs to (for "compare with previous revision" in the viewers).</summary>
    readonly Dictionary<string, (MailHeader Header, AttachmentInfo Attachment)> _attachmentOrigins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Earlier revisions of the same part in this correspondent's mails; the folder / a file dialog otherwise.</summary>
    async Task<string?> PickOlderRevisionAsync(Window owner, string path)
    {
        if (!_attachmentOrigins.TryGetValue(Path.GetFullPath(path), out var origin)) return RevisionLookup.PickFromDisk(owner, path);
        var candidates = RevisionService.FindCandidates(_cache, origin.Header, origin.Attachment.FileName);
        if (candidates.Count == 0)
        {
            Status(L.T("Bu parçanın başka bir revizyonu maillerde bulunamadı; dosyayı kendiniz seçin."));
            return RevisionLookup.Browse(owner, path);
        }
        var (choice, browse) = RevisionLookup.Choose(owner, candidates, origin.Attachment.FileName);
        if (browse) return RevisionLookup.Browse(owner, path);
        if (choice?.Header == null || choice.Attachment == null) return choice?.FilePath;
        return await DownloadAttachmentAsync(choice.Header, choice.Attachment);
    }

    async Task CompareAttachmentRevisionAsync(AttachmentInfo att)
    {
        var h = _current;
        var path = await DownloadAttachmentAsync(h, att);
        if (path == null || h == null) return;
        var oldPath = await PickOlderRevisionAsync(this, path);
        if (string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath)) return;
        await ShowRevisionDiffAsync(oldPath, path, h);
    }

    /// <summary>Runs the comparison and opens the result; "add to reply" answers the mail the file came from.</summary>
    async Task ShowRevisionDiffAsync(string oldPath, string newPath, MailHeader? mail)
    {
        Status(L.T("Revizyonlar karşılaştırılıyor..."));
        try
        {
            var report = await RevisionService.CompareAsync(oldPath, newPath, SkillWorker, _lifetime.Token);
            var win = new RevisionDiffWindow(report,
                onAddToReply: text =>
                {
                    if (mail != null && _current == mail && _currentContent != null) Reply(false, text);
                    else
                    {
                        try { Clipboard.SetText(text); Status(L.T("Fark özeti panoya kopyalandı (yanıtlanacak ileti açık değil).")); }
                        catch (System.Runtime.InteropServices.COMException) { }
                    }
                },
                aiRunner: async (prompt, ct) => await AiCompleteAsync(prompt, ct))
            { Owner = this };
            win.Show();
            Status(L.F("Revizyon karşılaştırma tamamlandı: {0} fark bulundu.", report.TotalChanges));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status(L.T("Revizyon karşılaştırılamadı: ") + ex.Message);
            MessageBox.Show(this, ex.Message, L.T("Revizyon Karşılaştırma Hatası"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        var outboxCount = _outbox.Items.Count(i => !i.IsUndoSend);
        Add(outboxCount > 0 ? L.F("Giden Kutusu ({0} ileti)…", outboxCount) : L.T("Giden Kutusu (Zamanlanmış)…"), (_, _) =>
 ShowOutboxWindow());
        var undoSendMenu = new MenuItem { Header = L.T("Göndermeyi geri alma süresi") };
        foreach (var (sec, label) in new[] { (0, L.T("Kapalı")), (5, L.F("{0} saniye", 5)), (10, L.F("{0} saniye", 10)), (20, L.F("{0} saniye", 20)), (30, L.F("{0} saniye", 30)) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _settings.UndoSendSeconds == sec };
            var s = sec;
            item.Click += (_, _) => { _settings.UndoSendSeconds = s; _settings.Save(); Status(L.F("Göndermeyi geri alma: {0}", label)); };
            undoSendMenu.Items.Add(item);
        }
        menu.Items.Add(undoSendMenu);
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

    void OnAddressBook(object sender, RoutedEventArgs e) => new ContactsWindow(pickMode: false) { Owner = this }.Also(FileViewers.ReturnFocusOnClose).Show();

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
        w.UndoSendAction = (acc, r, secs) => QueueSend(acc, r, secs);
        w.ScheduleSendAction = (acc, r, time) => ScheduleSend(acc, r, time);
        w.Closed += (_, _) => _openDrafts.Remove(req.DraftId);
        w.Sent += async (_, _) =>
        {
            Status(w.LastSendResult?.Warning ?? L.T("İleti sunucu tarafından kabul edildi."));
            if (w.LastSendResult != null && w.FollowUpDays > 0)
            {
                var due = DateTimeOffset.Now.AddDays(w.FollowUpDays);
                _followUpStore.Add(new FollowUpItem
                {
                    AccountId = accId,
                    MessageId = w.LastSendResult.MessageId ?? "",
                    Subject = w.SubjectText,
                    Recipients = w.ToAddressText,
                    SentDate = DateTimeOffset.Now,
                    DueDays = w.FollowUpDays,
                    DueDate = due
                });
            }
            if (w.AttachmentPaths.Count > 0)
            {
                if (_quoteStore.CheckSentMail(w.ToAddressText, req.InReplyTo, w.SubjectText, w.AttachmentPaths.Count))
                    _quoteDeskView?.Refresh();
            }
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

    /// <summary>Sends now (outbox "send now", agent-free paths). Errors are shown, never thrown.</summary>
    public async Task DoSendAsync(AccountConfig acc, ComposeRequest req)
    {
        try { await SendAndRecordAsync(acc, req); }
        catch (Exception ex) { Status(L.T("Gönderilemedi: ") + ex.Message); }
    }

    /// <summary>SMTP send + the work after a send (follow-up, quote desk, Sent folder). Throws only when the send itself failed.</summary>
    async Task SendAndRecordAsync(AccountConfig acc, ComposeRequest req)
    {
        Status(L.T("Gönderiliyor..."));
        var res = await _mail.SendAsync(acc, req, _lifetime.Token);
        Status(res.Warning ?? L.T("İleti sunucu tarafından kabul edildi."));
        try
        {
            if (req.FollowUpDays > 0)
                _followUpStore.Add(new FollowUpItem
                {
                    AccountId = acc.Id, MessageId = res.MessageId ?? "", Subject = req.Subject, Recipients = req.To,
                    SentDate = DateTimeOffset.Now, DueDays = req.FollowUpDays, DueDate = DateTimeOffset.Now.AddDays(req.FollowUpDays)
                });
            var attCount = req.AttachmentPaths?.Count ?? 0;
            if (attCount > 0 && _quoteStore.CheckSentMail(req.To, req.InReplyTo, req.Subject, attCount)) _quoteDeskView?.Refresh();
            var sent = _roots.SelectMany(r => r.Children).FirstOrDefault(f => f.AccountId == acc.Id && f.SortOrder == 2);
            if (sent != null) await SyncFolderAsync(sent, silent: true);
        }
        catch (Exception ex) { Status(L.T("İleti gönderildi; sonrasındaki kayıt işlemi tamamlanamadı: ") + ex.Message); }
    }

    /// <summary>"Undo send": the mail waits N seconds in the outbox (on disk); SMTP is not contacted until then.</summary>
    public void QueueSend(AccountConfig acc, ComposeRequest req, int undoSeconds)
    {
        if (undoSeconds <= 0) { _ = DoSendAsync(acc, req); return; }
        _outbox.Add(new OutboxItem { AccountId = acc.Id, Request = req, ScheduledTime = DateTimeOffset.Now.AddSeconds(undoSeconds), IsUndoSend = true });
        UpdateUndoBar();
    }

    public void ScheduleSend(AccountConfig acc, ComposeRequest req, DateTimeOffset scheduledTime)
    {
        _outbox.Add(new OutboxItem { AccountId = acc.Id, Request = req, ScheduledTime = scheduledTime });
        // The outbox now holds the mail; a local recovery copy would let it be sent twice by hand.
        try { new LocalDraftStore().Delete(req.DraftId); } catch (IOException) { }
        Status(L.F("İleti zamanlandı: {0:g}", scheduledTime.LocalDateTime));
    }

    void ShowOutboxWindow()
    {
        var w = new OutboxWindow(_outbox, (r, a) => OpenCompose(r, a, popOut: true), () => QueueTick(), this);
        w.ShowDialog();
    }

    OutboxItem? NewestUndo => _outbox.Items.Where(i => i.IsUndoSend && i.Status == OutboxStatus.Scheduled).OrderByDescending(i => i.ScheduledTime).FirstOrDefault();

    void UpdateUndoBar()
    {
        var undo = _outbox.Items.Where(i => i.IsUndoSend && i.Status == OutboxStatus.Scheduled).OrderByDescending(i => i.ScheduledTime).ToList();
        if (undo.Count == 0) { UndoSendBar.Visibility = Visibility.Collapsed; return; }
        var secs = Math.Max(0, (int)Math.Ceiling((undo[0].ScheduledTime - DateTimeOffset.Now).TotalSeconds));
        UndoSendText.Text = undo.Count == 1
            ? L.F("İleti {0} sn içinde gönderilecek...", secs)
            : L.F("{0} ileti gönderilmeyi bekliyor; sonuncusu {1} sn içinde gidecek...", undo.Count, secs);
        UndoSendBar.Visibility = Visibility.Visible;
    }

    void OnUndoSendClick(object sender, RoutedEventArgs e)
    {
        if (NewestUndo is not { } item) return;
        if (!_outbox.Remove(item.Id)) { Status(L.T("İleti gönderilmeye başladı; geri alınamadı.")); return; }
        UpdateUndoBar();
        Status(L.T("Gönderim geri alındı."));
        OpenCompose(item.Request, item.AccountId, popOut: true);
    }

    void OnSendNowClick(object sender, RoutedEventArgs e)
    {
        foreach (var i in _outbox.Items.Where(i => i.IsUndoSend && i.Status == OutboxStatus.Scheduled)) _outbox.Release(i.Id);
        QueueTick();
    }

    bool _queueBusy;
    async void QueueTick()
    {
        if (_queueBusy) return;
        _queueBusy = true;
        try
        {
            UpdateUndoBar();
            if (_outbox.GetPendingDue().Count == 0) return;
            await _outbox.ProcessDueSendsAsync(Acc, async (acc, item) =>
            {
                await SendAndRecordAsync(acc, item.Request);
                if (!item.IsUndoSend) Status(L.F("Zamanlanmış ileti gönderildi: {0}", item.Request.Subject));
            }, onFailed: OnOutboxFailed, ct: _lifetime.Token);
            UpdateUndoBar();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status(L.T("Giden Kutusu: ") + ex.Message); }
        finally { _queueBusy = false; }
    }

    void OnOutboxFailed(OutboxItem item, Exception ex)
    {
        Status(L.F("Gönderilemedi: {0} – {1}", item.Request.Subject, ex.Message));
        if (item.Status == OutboxStatus.Failed && item.IsUndoSend && _outbox.Remove(item.Id))
        {
            // The composer is already closed: give the mail back instead of losing it
            OpenCompose(item.Request, item.AccountId, popOut: true);
            return;
        }
        _tray?.ShowReminder(L.T("Giden Kutusu"), L.F("\"{0}\" gönderilemedi. Giden Kutusu'nu açıp kontrol edin.", item.Request.Subject), ShowOutboxWindow);
    }

    /// <summary>Mails whose send time passed while MailStudio was closed are never sent without asking.</summary>
    void AskHeldOutbox()
    {
        var held = _outbox.Held;
        var uncertain = _outbox.Items.Where(i => i.Status == OutboxStatus.Uncertain).ToList();
        if (held.Count == 0 && uncertain.Count == 0) return;
        if (App.SelfTesting) return;
        var lines = string.Join("\n", held.Take(8).Select(i => $"• {i.Request.Subject} → {i.Request.To} ({i.ScheduledTime.LocalDateTime:g})"));
        if (held.Count > 0)
        {
            var answer = MessageBox.Show(this,
                L.F("Program kapalıyken gönderim zamanı gelen {0} ileti var:\n\n{1}\n\nŞimdi gönderilsinler mi?\n(Hayır: Giden Kutusu'nda bekler, oradan gönderebilir, düzenleyebilir ya da silebilirsiniz.)", held.Count, lines),
                L.T("Giden Kutusu"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) { foreach (var i in held) _outbox.Release(i.Id); QueueTick(); }
        }
        if (uncertain.Count > 0)
            MessageBox.Show(this, L.F("{0} ileti gönderilirken program kapandı; gitmiş olabilir. Tekrar göndermeden önce Gönderilmiş Öğeler'i kontrol edin (Dosya → Giden Kutusu).", uncertain.Count),
                L.T("Gönderim sonucu belirsiz"), MessageBoxButton.OK, MessageBoxImage.Warning);
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

    void OnRules(object sender, RoutedEventArgs e)
    {
        _ruleStore.ReloadIfChanged();
        var sample = _messages.Take(200).ToList();
        var w = new RulesWindow(_ruleStore, sample, (prompt, ct) =>
        {
            var provider = CurrentProvider; var model = CurrentModel;
            return AiStream.CompleteAsync(token => provider.StreamAsync(prompt, model, token), ct);
        }, () => CurrentProvider.Name, null, RunRuleOnFolderAsync) { Owner = this };
        w.ShowDialog();
        Status(L.F("Kurallar: {0} kural kayıtlı.", _ruleStore.Rules.Count));
    }

    void OnCreateRuleFromMail(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var h = _current;
        var newRule = new MailRule
        {
            Name = L.F("{0} iletileri için kural", (h.FromName ?? h.FromAddress) ?? ""),
            Enabled = true,
            MatchAll = true,
            StopProcessing = true,
            Conditions = {
                new RuleCondition { Field = "from", Value = !string.IsNullOrEmpty(h.FromAddress) ? h.FromAddress : h.FromName }
            },
            Actions = {
                new RuleAction { Type = "move_to_folder", Value = "" }   // the user picks the folder
            }
        };
        var sample = _messages.Take(200).ToList();
        var w = new RulesWindow(_ruleStore, sample, (prompt, ct) =>
        {
            var provider = CurrentProvider; var model = CurrentModel;
            return AiStream.CompleteAsync(token => provider.StreamAsync(prompt, model, token), ct);
        }, () => CurrentProvider.Name, newRule, RunRuleOnFolderAsync) { Owner = this };
        w.ShowDialog();
    }

    void OnMailAddToQuoteDesk(object sender, RoutedEventArgs e)
    {
        var m = _current;
        if (m == null && MessageList.SelectedItem is MailHeader sel) m = sel;
        if (m == null) { Status(L.T("Önce listeden bir ileti seçin.")); return; }

        var content = _cache.GetBody(m.AccountId, m.Folder, m.Uid);
        var body = content?.Text ?? "";
        var atts = content?.Attachments?.Select(a => a.FileName).ToList() ?? new List<string>();

        var (isRfq, item) = QuoteStore.DetectRfq(m.Subject, body, m.FromDisplay ?? m.FromAddress ?? "", atts);
        item.SourceMail = $"{m.AccountId}|{m.Folder}|{m.Uid}";
        item.SourceSubject = m.Subject;
        item.SourceMessageId = m.MessageId;

        var dlg = new QuoteEditDialog(item, isNew: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _quoteStore.AddOrUpdate(dlg.Item);
            Status(L.F("Teklif masasına eklendi: {0}", dlg.Item.PartNumber.Length > 0 ? dlg.Item.PartNumber : dlg.Item.Customer));
            _quoteDeskView?.Refresh();
        }
    }

    void OnManageQuickSteps(object sender, RoutedEventArgs e)
    {
        var win = new QuickStepManagerWindow(_quickStepStore, _templateStore, this);
        win.ShowDialog();
        PopulateQuickStepButtons();
    }

    void OnQuickStepsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        QuickStepsContextMenu.Items.Clear();
        var steps = _quickStepStore.Steps;
        foreach (var s in steps)
        {
            var mi = new MenuItem
            {
                Header = L.T(s.Name),
                Icon = new TextBlock { Text = s.Icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 },
                InputGestureText = s.Shortcut
            };
            var curStep = s;
            mi.Click += async (_, _) => await ExecuteQuickStepAsync(curStep);
            QuickStepsContextMenu.Items.Add(mi);
        }

        if (steps.Count == 0)
        {
            QuickStepsContextMenu.Items.Add(new MenuItem { Header = L.T("(Hızlı adım yok)"), IsEnabled = false });
        }

        QuickStepsContextMenu.Items.Add(new Separator());
        var manage = new MenuItem { Header = L.T("Hızlı Adımları Yönet…"), Icon = new TextBlock { Text = "\uE713", FontFamily = new FontFamily("Segoe MDL2 Assets") } };
        manage.Click += OnManageQuickSteps;
        QuickStepsContextMenu.Items.Add(manage);
    }

    public void PopulateQuickStepButtons()
    {
        if (QuickStepsButtonPanel == null) return;
        QuickStepsButtonPanel.Children.Clear();
        foreach (var step in _quickStepStore.Steps)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("RibbonCmd"),
                Tag = step.Icon,
                Content = L.T(step.Name),
                ToolTip = string.IsNullOrEmpty(step.Shortcut) ? L.T(step.Description) : $"{L.T(step.Description)} ({step.Shortcut})"
            };
            var curStep = step;
            btn.Click += async (_, _) => await ExecuteQuickStepAsync(curStep);
            QuickStepsButtonPanel.Children.Add(btn);
        }
    }

    public async Task<bool> ExecuteQuickStepAsync(QuickStepItem step, MailHeader? targetMail = null)
    {
        var mail = targetMail ?? _current ?? MessageList.SelectedItems.OfType<MailHeader>().FirstOrDefault();
        if (mail == null)
        {
            Status(L.T("Önce listeden bir ileti seçin."));
            return false;
        }

        var acc = Acc(mail.AccountId);
        if (acc == null)
        {
            Status(L.T("İletinin hesabı bulunamadı."));
            return false;
        }

        Status(L.F("Hızlı Adım çalıştırılıyor: {0}", L.T(step.Name)));
        try
        {
        // moves and delete run last: afterwards the message is no longer in this folder
        foreach (var act in step.Actions.OrderBy(a => a.Type is QuickStepActionType.MoveToArchive or QuickStepActionType.MoveToFolder or QuickStepActionType.Delete ? 1 : 0))
        {
            switch (act.Type)
            {
                case QuickStepActionType.MarkRead:
                    await _mail.SetReadAsync(acc, mail, true);
                    mail.IsRead = true;
                    Reformat(mail);
                    UpdateCounts();
                    break;

                case QuickStepActionType.MarkUnread:
                    await _mail.SetReadAsync(acc, mail, false);
                    mail.IsRead = false;
                    Reformat(mail);
                    UpdateCounts();
                    break;

                case QuickStepActionType.Flag:
                    await _mail.SetFlaggedAsync(acc, mail, true);
                    mail.IsFlagged = true;
                    Reformat(mail);
                    break;

                case QuickStepActionType.Unflag:
                    await _mail.SetFlaggedAsync(acc, mail, false);
                    mail.IsFlagged = false;
                    Reformat(mail);
                    break;

                case QuickStepActionType.MoveToArchive:
                    var folders = _roots.SelectMany(r => r.Children).Where(f => f.AccountId == mail.AccountId).ToList();
                    var arch = folders.FirstOrDefault(f => f.SortOrder == 5) ?? folders.FirstOrDefault(f => f.SortOrder == 60);
                    if (arch != null)
                    {
                        await _mail.MoveAsync(acc, mail, arch.FullName);
                        _messages.Remove(mail);
                        ClearReading();
                    }
                    break;

                case QuickStepActionType.MoveToFolder:
                    if (!string.IsNullOrWhiteSpace(act.Parameter))
                    {
                        var target = _roots.SelectMany(r => r.Children)
                            .FirstOrDefault(f => f.AccountId == mail.AccountId && (f.FullName.Equals(act.Parameter, StringComparison.OrdinalIgnoreCase) || f.Name.Equals(act.Parameter, StringComparison.OrdinalIgnoreCase)));
                        if (target != null)
                        {
                            await _mail.MoveAsync(acc, mail, target.FullName);
                            _messages.Remove(mail);
                            ClearReading();
                        }
                    }
                    break;

                case QuickStepActionType.Delete:
                    await DeleteMessagesAsync(new[] { mail });
                    break;

                case QuickStepActionType.CreateTask:
                    var key = $"{mail.AccountId}|{mail.Folder}|{mail.Uid}";
                    if (_tasks.FindBySource(key) is not { Completed: false })
                    {
                        var task = TasksView.FromMail(mail, DateTime.Today);
                        if (!string.IsNullOrWhiteSpace(act.Parameter))
                        {
                            task.Category = act.Parameter;
                        }
                        _tasks.Save(task);
                    }
                    break;

                case QuickStepActionType.ReplyWithTemplate:
                    EmailTemplate? tmpl = null;
                    if (!string.IsNullOrWhiteSpace(act.Parameter))
                    {
                        tmpl = _templateStore.FindById(act.Parameter) ??
                               _templateStore.Templates.FirstOrDefault(t => t.Title.Equals(act.Parameter, StringComparison.OrdinalIgnoreCase));
                    }
                    if (tmpl == null) { Status(L.F("Şablon bulunamadı: {0}", act.Parameter ?? "")); break; }
                    if (tmpl != null)
                    {
                        var sig = _store.Accounts.FirstOrDefault(a => a.Id == mail.AccountId)?.Signature ?? "";
                        var sigText = System.Text.RegularExpressions.Regex.Replace(sig, "<.*?>", string.Empty).Trim();
                        var ctx = TemplateContext.Create(
                            to: $"\"{mail.FromName}\" <{mail.FromAddress}>",
                            subject: mail.Subject,
                            signature: sigText,
                            bodyForPartExtraction: _currentContent?.Text ?? mail.Preview
                        );
                        var rendered = TemplateEngine.Render(tmpl.Body, ctx);
                        var content = _currentContent ?? _cache.GetBody(mail.AccountId, mail.Folder, mail.Uid) ?? new MailContent { Html = "<p>" + System.Net.WebUtility.HtmlEncode(mail.Preview) + "</p>" };
                        Reply(mail, content, all: false, body: rendered);
                    }
                    break;

                case QuickStepActionType.SetCategory:
                    if (!string.IsNullOrWhiteSpace(act.Parameter))
                    {
                        _cache.SetCategory(mail.AccountId, mail.Folder, mail.Uid, act.Parameter);
                        mail.Category = act.Parameter;
                        Reformat(mail);
                    }
                    break;
            }
        }

        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Status(L.F("Hızlı Adım tamamlanamadı: {0} – {1}", L.T(step.Name), ex.Message));
            return false;
        }
        Status(L.F("Hızlı Adım tamamlandı: {0}", L.T(step.Name)));
        return true;
    }

    // =========================================================================
    // Snooze (Erteleme)
    // =========================================================================

    void OnSnoozeRibbonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var thisEve = new MenuItem { Header = L.T("Bugün akşam (18:00)") };
        thisEve.Click += (_, _) => SnoozeSelected(SnoozeStore.GetThisEvening());
        var tomorrow = new MenuItem { Header = L.T("Yarın sabah (09:00)") };
        tomorrow.Click += (_, _) => SnoozeSelected(SnoozeStore.GetTomorrowMorning());
        var nextWeek = new MenuItem { Header = L.T("Gelecek hafta (Pazartesi 09:00)") };
        nextWeek.Click += (_, _) => SnoozeSelected(SnoozeStore.GetNextWeek());
        var custom = new MenuItem { Header = L.T("Tarih ve saat seç…") };
        custom.Click += (_, _) => OnSnoozeCustomDate(this, new RoutedEventArgs());

        menu.Items.Add(thisEve);
        menu.Items.Add(tomorrow);
        menu.Items.Add(nextWeek);
        menu.Items.Add(new Separator());
        menu.Items.Add(custom);

        var selected = MessageList.SelectedItems.OfType<MailHeader>().ToList();
        if (selected.Count == 0 && _current != null) selected = new() { _current };
        if (selected.Any(m => _snoozeStore.IsSnoozed(m.AccountId, m.Folder, m.Uid)))
        {
            menu.Items.Add(new Separator());
            var un = new MenuItem { Header = L.T("Ertelemeyi Kaldır") };
            un.Click += (_, _) => UnsnoozeSelected();
            menu.Items.Add(un);
        }

        menu.IsOpen = true;
    }

    void OnSnoozeSubmenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = MessageList.SelectedItems.OfType<MailHeader>().ToList();
        if (selected.Count == 0 && _current != null) selected = new() { _current };
        bool anySnoozed = selected.Any(m => _snoozeStore.IsSnoozed(m.AccountId, m.Folder, m.Uid));
        if (UnsnoozeContextMenuItem != null)
        {
            UnsnoozeContextMenuItem.Visibility = anySnoozed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void OnSnoozeThisEvening(object sender, RoutedEventArgs e) => SnoozeSelected(SnoozeStore.GetThisEvening());
    void OnSnoozeTomorrowMorning(object sender, RoutedEventArgs e) => SnoozeSelected(SnoozeStore.GetTomorrowMorning());
    void OnSnoozeNextWeek(object sender, RoutedEventArgs e) => SnoozeSelected(SnoozeStore.GetNextWeek());

    void OnSnoozeCustomDate(object sender, RoutedEventArgs e)
    {
        var picked = SnoozeCustomDateDialog.Show(this);
        if (picked.HasValue)
        {
            SnoozeSelected(picked.Value);
        }
    }

    void OnUnsnooze(object sender, RoutedEventArgs e) => UnsnoozeSelected();

    public void SnoozeSelected(DateTimeOffset until)
    {
        var selected = MessageList.SelectedItems.OfType<MailHeader>().ToList();
        if (selected.Count == 0 && _current != null) selected = new() { _current };
        if (selected.Count == 0) return;

        foreach (var m in selected)
        {
            _snoozeStore.Snooze(m.AccountId, m.Folder, m.Uid, until, m.Subject, m.FromName, m.IsRead);
            m.IsSnoozed = true;
            m.SnoozedUntil = until;
        }
        _view.Refresh();
        UpdateCounts();
        Status(selected.Count == 1
            ? L.F("İleti ertelendi ({0:g}): {1}", until, selected[0].Subject)
            : L.F("{0} ileti ertelendi ({1:g})", selected.Count, until));
    }

    public void UnsnoozeSelected()
    {
        var selected = MessageList.SelectedItems.OfType<MailHeader>().ToList();
        if (selected.Count == 0 && _current != null) selected = new() { _current };
        if (selected.Count == 0) return;

        foreach (var m in selected)
        {
            _snoozeStore.Unsnooze(m.AccountId, m.Folder, m.Uid);
            m.IsSnoozed = false;
            m.SnoozedUntil = null;
        }
        _view.Refresh();
        UpdateCounts();
        Status(L.F("Erteleme kaldırıldı ({0} ileti).", selected.Count));
    }

    /// <summary>Snoozed mail whose time has come: back in the list, marked unread (on the server too) and announced.
    /// The mail keeps its real date (the cache is never rewritten).</summary>
    public async void CheckSnoozeDue()
    {
        var due = _snoozeStore.CheckDue(DateTimeOffset.Now);
        if (due.Count == 0) return;
        foreach (var item in due)
        {
            var inMemory = _messages.FirstOrDefault(m =>
                string.Equals(m.AccountId, item.AccountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Folder, item.Folder, StringComparison.OrdinalIgnoreCase) && m.Uid == item.Uid);
            if (inMemory != null) { inMemory.IsSnoozed = false; inMemory.SnoozedUntil = null; }
            if (!item.RestoreAsUnread) continue;
            var header = inMemory ?? _cache.GetHeaders(item.AccountId, item.Folder, 5000).FirstOrDefault(m => m.Uid == item.Uid);
            if (header == null || Acc(item.AccountId) is not { } acc) continue;
            try
            {
                await _mail.SetReadAsync(acc, header, false);
                header.IsRead = false;
            }
            catch (Exception ex) { Status(L.T("Ertelenen ileti okunmadı yapılamadı: ") + ex.Message); }
        }
        _view.Refresh();
        UpdateCounts();
        var text = due.Count == 1 ? L.F("Ertelenen mail geri döndü: {0}", due[0].Subject) : L.F("Ertelenen {0} ileti geri döndü.", due.Count);
        Status(text);
        _tray?.ShowReminder(L.T("Ertelenen ileti"), text, ShowFromTray);
    }

    // ---------------- Search Folders ----------------

    void OnFolderTreeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var sel = FolderTree.SelectedItem as FolderNode;
        var isSearch = sel != null && sel.IsSearchFolder;
        var isCustomSearch = isSearch && sel?.SearchFolderId != null && _searchFolderStore.Find(sel.SearchFolderId)?.IsBuiltIn != true;

        if (EditSearchFolderMenuItem != null)
        {
            EditSearchFolderMenuItem.Visibility = isSearch ? Visibility.Visible : Visibility.Collapsed;
        }
        if (DeleteSearchFolderMenuItem != null)
        {
            DeleteSearchFolderMenuItem.Visibility = isCustomSearch ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void OnNewSearchFolderClick(object sender, RoutedEventArgs e)
    {
        var item = Views.SearchFolders.SearchFolderDialog.ShowDialog(this);
        if (item != null)
        {
            _searchFolderStore.AddOrUpdate(item);
            _ = RefreshSearchFoldersInTreeAsync(item.Id);
        }
    }

    void OnEditSearchFolderClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node || !node.IsSearchFolder || node.SearchFolderId == null) return;
        var existing = _searchFolderStore.Find(node.SearchFolderId);
        if (existing == null) return;
        var updated = Views.SearchFolders.SearchFolderDialog.ShowDialog(this, existing);
        if (updated != null)
        {
            _searchFolderStore.AddOrUpdate(updated);
            _ = RefreshSearchFoldersInTreeAsync(updated.Id);
        }
    }

    void OnDeleteSearchFolderClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node || !node.IsSearchFolder || node.SearchFolderId == null) return;
        var existing = _searchFolderStore.Find(node.SearchFolderId);
        if (existing == null || existing.IsBuiltIn) return;

        var res = MessageBox.Show(this,
            L.F("\"{0}\" arama klasörünü silmek istediğinizden emin misiniz?", existing.Name),
            L.T("Arama Klasörünü Sil"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            _searchFolderStore.Delete(existing.Id);
            _ = RefreshSearchFoldersInTreeAsync();
        }
    }

    async Task RefreshSearchFoldersInTreeAsync(string? selectFolderId = null)
    {
        var searchRoot = _roots.FirstOrDefault(r => r.IsSearchFolderRoot);
        if (searchRoot == null) return;

        searchRoot.Children.Clear();
        FolderNode? targetNode = null;
        foreach (var sf in _searchFolderStore.Folders)
        {
            var fn = new FolderNode
            {
                AccountId = "",
                FullName = $"SEARCH_{sf.Id}",
                Name = L.T(sf.Name),
                Icon = sf.Icon,
                IsSearchFolder = true,
                SearchFolderId = sf.Id,
                UnreadCount = _searchFolderStore.CountMatching(sf, _cache, unreadOnly: true)
            };
            searchRoot.Children.Add(fn);
            if (selectFolderId != null && sf.Id == selectFolderId) targetNode = fn;
        }

        if (targetNode != null)
        {
            SelectFolder(targetNode);
        }
        else if (_currentFolder?.IsSearchFolder == true)
        {
            var fallback = _roots.FirstOrDefault(r => r.IsUnifiedInbox);
            if (fallback != null) SelectFolder(fallback);
        }
        await Task.CompletedTask;
    }

    async Task RunRuleOnFolderAsync(MailRule rule)
    {
        if (_currentFolder == null || _currentFolder.IsUnifiedInbox || _currentFolder.IsSearchFolder || _currentFolder.IsSearchFolderRoot)
        { MessageBox.Show(this, L.T("Kuralı çalıştırmak için önce bir hesap klasörü seçin."), L.T("Kurallar")); return; }
        if (RuleEngine.Validate(rule) is { } problem) { MessageBox.Show(this, problem, L.T("Kurallar")); return; }
        var acc = Acc(_currentFolder.AccountId);
        if (acc == null) return;
        var list = _messages.Where(m => m.AccountId == acc.Id).ToList();
        var preview = list.Count(m => RuleEngine.Matches(new MailRule { Enabled = true, MatchAll = rule.MatchAll, Conditions = rule.Conditions }, m, null, _cache.GetAttachments(m.AccountId, m.Folder, m.Uid)));
        if (MessageBox.Show(this, L.F("\"{0}\" kuralı \"{1}\" klasöründeki {2} iletiye uygulanacak (önizlemede {3} ileti uyuyor).\n\nEylemler: {4}\n\nDevam edilsin mi?",
                rule.Name, _currentFolder.Name, list.Count, preview, string.Join(", ", rule.Actions.Select(RuleEngine.DescribeAction))),
                L.T("Klasörde şimdi çalıştır"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var runnable = rule.Clone(); runnable.Enabled = true;
        int affected = 0, failed = 0;
        foreach (var m in list)
        {
            try
            {
                var results = await RuleEngine.ApplyRulesAsync(new[] { runnable }, m, acc, _mail, _cache, _tasks, null, _lifetime.Token);
                if (results.Count > 0) affected++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { failed++; }
        }
        LoadMessagesFromCache();
        Status(failed == 0 ? L.F("Kural çalıştırıldı: {0} ileti etkilendi.", affected) : L.F("Kural çalıştırıldı: {0} ileti etkilendi, {1} iletide hata oluştu.", affected, failed));
    }

    async Task RunRulesOnFreshAsync(AccountConfig acc, List<MailHeader> fresh)
    {
        try
        {
            _ruleStore.ReloadIfChanged();
            var rules = _ruleStore.Rules.Where(r => r.Enabled && RuleEngine.Validate(r) == null).ToList();
            if (rules.Count == 0) return;
            bool anyMoved = false;
            foreach (var h in fresh)
            {
                try
                {
                    var res = await RuleEngine.ApplyRulesAsync(rules, h, acc, _mail, _cache, _tasks, null, _lifetime.Token);
                    if (res.Any(r => r.Moved)) anyMoved = true;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Status(L.F("Kural uygulanamadı ({0}): {1}", h.Subject, ex.Message)); }
            }
            if (anyMoved) LoadMessagesFromCache();
        }
        catch (Exception ex) { Status(L.T("Kurallar: ") + ex.Message); }
    }

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

using System.Windows;
using System.Windows.Threading;

namespace MailStudio;

public partial class App : Application
{
    Services.SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // Demo mailbox (no account, nothing is sent): MailStudio.exe --demo
        var demo = e.Args.Contains("--demo");
        if (demo) { Services.DemoData.UseDemoFolder(); Environment.SetEnvironmentVariable("MAILSTUDIO_DEMO", "1"); }
        // Language: restart handoff (wait for the previous instance), then UI culture.
        var restartAt = Array.IndexOf(e.Args, "--restart-wait");
        if (restartAt >= 0 && restartAt + 1 < e.Args.Length && int.TryParse(e.Args[restartAt + 1], out var oldPid))
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); old.WaitForExit(20000); } catch { }
        L.Init(Services.AppSettings.Shared.Language);
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = L.Culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = L.Culture;
        System.Threading.Thread.CurrentThread.CurrentCulture = L.Culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = L.Culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag)));
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        Services.ThemeManager.Apply(Services.ThemeManager.Parse(Services.AppSettings.Shared.Theme));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) Services.ThemeManager.ApplyTitleBar(w); }));
        // Developer test: MailStudio.exe --ai-test Codex  -> writes the answer to ai-test.txt
        if (e.Args.Length >= 2 && e.Args[0] == "--ai-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunAiTestAsync(e.Args[1], e.Args.Length > 2 ? e.Args[2] : null);
            return;
        }
        // Developer preview: MailStudio.exe --ui-preview  -> opens the account, message and AI suggestion windows with sample data (saves nothing, sends nothing)
        if (e.Args.Length >= 1 && e.Args[0] == "--ui-preview") { OpenUiPreview(); return; }
        if (Environment.GetEnvironmentVariable("MAILSTUDIO_DEV_PYTHON") is { Length: > 0 } devPython) Services.TechnicalWorker.DevelopmentPython = devPython;
        // Office editor: MailStudio.exe --edit <file>  (Word / Excel / PowerPoint)
        if (e.Args.Length >= 2 && e.Args[0] == "--edit" && System.IO.File.Exists(e.Args[1]))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            var file = System.IO.Path.GetFullPath(e.Args[1]);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { if (await Views.FileViewers.OpenEditorAsync(file, null) == null) Shutdown(); }
                catch (Exception ex) { MessageBox.Show(L.T("Düzenleyici açılamadı:\n") + ex.Message, "MailStudio"); Shutdown(); }
            }));
            return;
        }
        // Single file viewing: MailStudio.exe --view <file>  (the mail window does not open; exits when the viewer closes)
        if (e.Args.Length >= 2 && e.Args[0] == "--view" && System.IO.File.Exists(e.Args[1]))
        {
            var viewer = Views.FileViewers.Create(System.IO.Path.GetFullPath(e.Args[1]), Views.FileViewers.DefaultAi());
            if (viewer == null) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Args[1]) { UseShellExecute = true }); Shutdown(); return; }
            viewer.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShutdownMode = ShutdownMode.OnLastWindowClose;   // the window changes while an Office document switches to the PDF view
            viewer.Show(); return;
        }
        _instance = new Services.SingleInstance(Services.AppPaths.Root);
        if (!_instance.IsPrimary)
        {
            if (!e.Args.Contains("--background")) _instance.RestorePrimary();
            if(!e.Args.Contains("--background"))
            {
                var anotherVersion=false;
                foreach(var process in System.Diagnostics.Process.GetProcessesByName("MailStudio"))
                {
                    using(process)try{if(process.Id!=Environment.ProcessId&&!string.Equals(process.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))anotherVersion=true;}catch(System.ComponentModel.Win32Exception){}
                }
                if(anotherVersion)MessageBox.Show(L.T("MailStudio'nun başka bir sürümü açık. Yeni sürüm henüz başlamadı.\n\nAçık MailStudio'da Dosya → Çıkış seçin; sonra bu sürümü yeniden açın."),L.T("Eski sürüm açık"),MessageBoxButton.OK,MessageBoxImage.Information);
            }
            Shutdown(); return;
        }
        base.OnStartup(e);
        if (demo) { var demoStore = new Services.AccountStore(); demoStore.Load(); var demoCache = new Services.MailCache(); demoCache.Init(); Services.DemoData.Seed(demoStore, demoCache); }
        var window = new MainWindow(startInTray: e.Args.Contains("--background"));
        MainWindow = window;
        _instance.Listen(Dispatcher, window.ShowFromTray);
        window.Show();
        var showcaseAt = Array.IndexOf(e.Args, "--showcase");
        if (demo && showcaseAt >= 0 && showcaseAt + 1 < e.Args.Length)
        {
            var target = System.IO.Path.GetFullPath(e.Args[showcaseAt + 1]);
            window.ContentRendered += async (_, _) => await Services.Showcase.RunAsync(window, target);
        }
        var videoAt = Array.IndexOf(e.Args, "--showcase-video");
        if (demo && videoAt >= 0 && videoAt + 1 < e.Args.Length)
        {
            var target = System.IO.Path.GetFullPath(e.Args[videoAt + 1]);
            window.ContentRendered += async (_, _) => await Services.ShowcaseVideo.RunAsync(window, target);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    async Task RunAiTestAsync(string name, string? model)
    {
        var outFile = Path.Combine(Services.AppPaths.Root, "ai-test.txt");
        Services.Ai.IAiProvider p = name switch
        {
            "Claude" => new Services.Ai.ClaudeCliProvider(),
            "Codex" => new Services.Ai.CodexCliProvider(),
            "Gemini" => new Services.Ai.GeminiCliProvider(),
            _ => new Services.Ai.OllamaProvider(() => "http://localhost:11434"),
        };
        var sb = new System.Text.StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var mcp = Environment.GetEnvironmentVariable("MAILSTUDIO_TEST_MCP") == "1" ? Services.McpLocator.Find() : null;
            var question = mcp != null ? L.T("list_accounts aracını çağır ve sonucu tek cümleyle söyle.") : L.T("Tek cümleyle kendini tanıt.");
            var prompt = Services.Ai.AiPrompts.Build(Array.Empty<Services.Ai.ChatTurn>(), question, null, mcp != null);
            await foreach (var c in p.StreamAsync(prompt, string.IsNullOrEmpty(model) ? null : model, CancellationToken.None, mcp)) sb.Append(c);
            File.WriteAllText(outFile, $"OK {name} {sw.Elapsed.TotalSeconds:0.0}s\n{sb}");
        }
        catch (Exception ex) { File.WriteAllText(outFile, $"FAIL {name}: {ex.Message}"); }
        Shutdown();
    }

    void OpenUiPreview()
    {
        var settings = Services.AppSettings.Shared;
        var oauth = new Services.OAuthService(() => settings);
        var account = new Models.AccountConfig { DisplayName = L.T("Önizleme"), Email = "onizleme@ornek.test", ImapHost = "imap.ornek.test", SmtpHost = "smtp.ornek.test" };
        var accountWindow = new Views.AccountWindow(account, settings, oauth);
        var compose = new Views.ComposeWindow(new[] { account }, account.Id, new Models.ComposeRequest { To = "deniz@ornek.test; ", Subject = L.T("RE: Teklif talebi · montaj parçaları"), Body = L.T("Merhaba,\n\n25 adet bağlantı plakası için teslim süresi 2 haftadır.\n\nSaygılarımla"), QuotedHtml = "<p>Merhaba, ekteki parçalar için fiyat ve teslim süresi paylaşabilir misiniz?</p>" },
            new Services.MailService(new Services.MailCache(), oauth), (_, _) => Task.FromResult(""), () => "Ollama");
        compose.Closed += (_, _) => Shutdown();
        compose.Show(); accountWindow.Show();
        new Views.RewritePreviewWindow(compose, L.T("25 adet parça için teslim süresi 2 haftadır."), L.T("Sipariş kapsamındaki 30 adet parçanız 3 hafta içinde teslim edilecektir."), protectNumbers: true).Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(Services.AppPaths.Root, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "MailStudio - " + L.T("Hata"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Calendar;
using MailStudio.Views;
using MailStudio.Views.Calendar;
using MailStudio.Views.Quotes;
using MailStudio.Views.Templates;
using MailStudio.Views.QuickSteps;
using MailStudio.Views.Snooze;

namespace MailStudio;

/// <summary>
/// Self-test: `MailStudio.exe --demo --selftest &lt;folder&gt;` drives the REAL app on the demo mailbox (isolated data folder,
/// nothing is sent) through every module and window, takes real screenshots (PrintWindow, WebView2 included), checks
/// close buttons, native-window overlap ("airspace"), untranslated texts and unhandled errors, auto-answers message
/// boxes, and writes selftest.log + summary.txt. Exits when done.
/// </summary>
public partial class MainWindow
{
    StreamWriter? _stLog;
    string _stDir = "";
    int _stPass, _stFail, _stWarn;
    readonly List<string> _stFailures = new();
    volatile bool _stRunning;
    int _stShot;

    public async Task RunSelfTestAsync(string dir)
    {
        _stDir = dir; Directory.CreateDirectory(dir);
        _stLog = new StreamWriter(Path.Combine(dir, "selftest.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        Log("INFO", $"{L.ProductName} {typeof(MainWindow).Assembly.GetName().Version} self-test · language={L.Language} · data={AppPaths.Root} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (Environment.GetEnvironmentVariable("MAILSTUDIO_DATA_DIR") is not { Length: > 0 } || _store.Accounts.Any(a => a.Id != DemoData.AccountId))
        {
            Fail("safety", "Self-test needs an isolated demo data folder (MAILSTUDIO_DATA_DIR) and only the demo account; stopped.");
            FinishSelfTest(); return;
        }
        _stRunning = true;
        App.SelfTesting = true;
        Application.Current.DispatcherUnhandledException += (_, e) => { Fail("unhandled", e.Exception.ToString()); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { Warn("unobserved-task", e.Exception.GetBaseException().Message); e.SetObserved(); };
        StartDialogWatchdog();

        var steps = new (string Name, Func<Task> Run)[]
        {
            ("mail: main window and list", StepMailList),
            ("mail: every message in the reading pane", StepEveryMessage),
            ("mail: invitation bar and accept", StepInvitation),
            ("mail: address links and address book", StepAddressBook),
            ("mail: new mail, reply and message window", StepCompose),
            ("mail: attachments open in the viewers", StepAttachments),
            ("mail: revision comparison (candidates, chooser, Excel/DXF/STEP/PDF)", StepRevisionDiff),
            ("calendar: every view, overlap with the mail view", StepCalendar),
            ("calendar: appointment form", StepEventForm),
            ("tasks: list, quick add, mail to task", StepTasks),
            ("quote desk: list, search, status, Excel export, RFQ detection, dialog, airspace", StepQuoteDesk),
            ("templates and quick steps: template insertion, manager dialog, macro execution", StepTemplatesAndQuickSteps),
            ("mail snooze: hide, filter, wake up unread with the real date", StepSnooze),
            ("unified inbox and search folders: all inboxes node, saved searches, criteria execution", StepUnifiedInboxAndSearchFolders),
            ("newsletters and unsubscribe: header parsing, unsubscribe bar, filter tab, move to newsletters rule", StepNewslettersAndUnsubscribe),
            ("calendar sync: off by default, consent, no request without an address, calendar choice", StepCalendarSync),
            ("rules: validation and real execution on new mail", StepRules),
            ("undo send and send later: undo, failed send, outbox window", StepUndoSendAndOutbox),
            ("follow-up: overdue task, reminder draft, reply closes it", StepFollowUp),
            ("small polish: large attachment warning, contact card, at-mentions", StepSmallPolish),
            ("reminders window", StepReminders),
            ("other windows: accounts, signatures, rules, skills, agents", StepOtherWindows),
            ("dark theme screenshots", StepDarkTheme),
        };
        foreach (var (name, run) in steps)
        {
            if (_stopRequested) break;
            Log("STEP", name);
            var sw = Stopwatch.StartNew();
            try
            {
                var task = run();
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(120))) != task) Fail(name, "timed out after 120 s");
                else await task;
            }
            catch (Exception ex) { Fail(name, ex.ToString()); }
            CloseStrayWindows(name);
            Log("INFO", $"{name}: {sw.Elapsed.TotalSeconds:0.0} s");
        }
        FinishSelfTest();
    }

    // ===================================================================== logging

    void Log(string level, string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {text}";
        lock (_stFailures) _stLog?.WriteLine(line);
    }
    void Pass(string what) { _stPass++; Log("PASS", what); }
    void Fail(string what, string why) { _stFail++; lock (_stFailures) _stFailures.Add($"{what}: {why.Split('\n')[0]}"); Log("FAIL", $"{what}: {why}"); }
    void Warn(string what, string why) { _stWarn++; Log("WARN", $"{what}: {why}"); }
    void Check(bool ok, string what, string why = "") { if (ok) Pass(what); else Fail(what, why.Length > 0 ? why : "check failed"); }

    void FinishSelfTest()
    {
        _stRunning = false;
        var summary = new StringBuilder();
        summary.AppendLine($"MailStudio self-test {DateTime.Now:yyyy-MM-dd HH:mm} · {L.Language}");
        summary.AppendLine($"PASS {_stPass} · FAIL {_stFail} · WARN {_stWarn}");
        foreach (var f in _stFailures) summary.AppendLine("FAIL " + f);
        File.WriteAllText(Path.Combine(_stDir, "summary.txt"), summary.ToString(), new UTF8Encoding(false));
        Log("INFO", $"done: PASS {_stPass} FAIL {_stFail} WARN {_stWarn}");
        _stLog?.Dispose(); _stLog = null;
        _exitRequested = true;
        var code = _stFail == 0 ? 0 : 1;
        Dispatcher.BeginInvoke(() => Application.Current.Shutdown(code));
        // A modal box or a stuck window must not keep the test process alive
        new Thread(() => { Thread.Sleep(15000); Environment.Exit(code); }) { IsBackground = true }.Start();
    }

    // ===================================================================== helpers

    static Task Wait(int ms) => Task.Delay(ms);

    static async Task<bool> Until(Func<bool> ok, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (ok()) return true; await Task.Delay(100); }
        return ok();
    }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Real pixels of the window (WebView2 and other native parts included).</summary>
    void Shot(Window w, string name)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            if (width <= 0 || height <= 0) return;
            using var bmp = new System.Drawing.Bitmap(width, height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
            }
            var file = $"{++_stShot:00}-{string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'))}.png";
            bmp.Save(Path.Combine(_stDir, file), System.Drawing.Imaging.ImageFormat.Png);
            Log("SHOT", file);
            // A locked screen or a switched-off display gives white/black pictures: WPF does not render then.
            if (IsBlank(bmp))
            {
                Fail("screenshot " + name, $"{file} is empty (one colour). The screen is probably locked or asleep - keep it on while the test runs.");
                if (++_blankShots >= 3 && !_blankAborted)
                {
                    _blankAborted = true;
                    Fail("self-test", "three empty screenshots: stopped (results without pictures prove nothing).");
                    _stopRequested = true;
                }
            }
        }
        catch (Exception ex) { Warn("screenshot " + name, ex.Message); }
    }

    int _blankShots; bool _blankAborted, _stopRequested;

    /// <summary>
    /// True when the window content (below the title bar, inside the frame) has at most 3 exact colours - what
    /// PrintWindow returns while the session is locked. A white drawing or page still has anti-aliased text and lines.
    /// </summary>
    static bool IsBlank(System.Drawing.Bitmap bmp)
    {
        if (bmp.Width < 120 || bmp.Height < 120) return false;
        var rect = new System.Drawing.Rectangle(12, 40, bmp.Width - 24, bmp.Height - 52);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var colours = new HashSet<int>();
            var row = new int[rect.Width];
            for (int y = 0; y < rect.Height; y += 2)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rect.Width);
                foreach (var px in row) { colours.Add(px & 0xFFFFFF); if (colours.Count > 3) return false; }
            }
            return true;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Unread badges in the folder pane must be fully visible (no clipping at the pane edge, no sideways scrolling).</summary>
    void CheckFolderBadges(string where)
    {
        FolderTree.UpdateLayout();
        var sv = Tree(FolderTree).OfType<ScrollViewer>().FirstOrDefault();
        Check(sv == null || sv.ScrollableWidth < 1, $"{where}: folder pane has no sideways scrolling", $"scrollable width {sv?.ScrollableWidth:0}");
        var badges = Tree(FolderTree).OfType<Border>().Where(b => b.Name == "cnt" && b.IsVisible).ToList();
        var clipped = badges.Where(b =>
        {
            var r = b.TransformToAncestor(FolderTree).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
            return r.Right > FolderTree.ActualWidth + 0.5 || r.Left < 0;
        }).ToList();
        Check(badges.Count > 0 && clipped.Count == 0, $"{where}: {badges.Count} unread badges fully visible in the folder pane", $"{clipped.Count} badge(s) cut off at the pane edge");
    }

    static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop(); yield return d;
            var n = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (var i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    static string TextOf(object? content) => content switch
    {
        string s => s,
        TextBlock t => t.Text,
        Panel p => string.Join(" ", p.Children.OfType<TextBlock>().Select(x => x.Text)),
        _ => ""
    };

    /// <summary>A native child (WebView2) covered by a WPF overlay is drawn ON TOP of it - the bug where the calendar
    /// showed the last mail through it.</summary>
    void CheckAirspace(Window w, string where)
    {
        if (!w.IsVisible || w.Content is not FrameworkElement root) return;
        var natives = Tree(w).OfType<HwndHost>().Where(h => h.IsVisible && h.ActualWidth > 1).ToList();
        var overlays = Tree(w).OfType<FrameworkElement>().Where(e => e.IsVisible && e.ActualWidth > 20 && e.ActualHeight > 20 &&
            (Panel.GetZIndex(e) >= 5 || (e.Name ?? "").EndsWith("Overlay", StringComparison.Ordinal) || e.Name == "PimHost")).ToList();
        foreach (var n in natives)
        {
            var nr = Bounds(n, root);
            foreach (var o in overlays)
            {
                if (IsAncestor(o, n) || IsAncestor(n, o)) continue;
                var or = Bounds(o, root);
                var overlap = Rect.Intersect(nr, or);
                if (!overlap.IsEmpty && overlap.Width * overlap.Height > 400)
                    Fail($"{where}: native view under overlay", $"{n.GetType().Name} '{n.Name}' shows through '{(o.Name is { Length: > 0 } nm ? nm : o.GetType().Name)}' ({overlap.Width:0}×{overlap.Height:0} px)");
            }
        }
        if (natives.Count > 0 || overlays.Count > 0) Pass($"{where}: airspace checked ({natives.Count} native, {overlays.Count} overlay)");
    }

    /// <summary>List rows show user data (mail subjects, names): not part of the interface text check.</summary>
    static bool IsInsideItemsControl(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ListBoxItem or ListViewItem or DataGridRow or TreeViewItem or ComboBoxItem) return true;
        return false;
    }

    static bool IsAncestor(DependencyObject a, DependencyObject b)
    {
        for (var d = b; d != null; d = VisualTreeHelper.GetParent(d)) if (d == a) return true;
        return false;
    }

    static Rect Bounds(FrameworkElement e, FrameworkElement root)
    {
        try { return e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    /// <summary>English UI: texts that are still Turkish although a translation exists.</summary>
    void CheckTexts(Window w, string where)
    {
        if (!L.IsEnglish) return;
        var bad = new SortedSet<string>();
        void Add(object? o) { if (o is string s && s.Trim().Length > 0 && L.Has(s) && L.T(s) != s) bad.Add(s); }
        Add(w.Title);
        foreach (var d in Tree(w))
        {
            switch (d)
            {
                case TextBlock tb when tb.IsVisible && !UiTranslator.GetSkip(tb): Add(tb.Text); break;
                case HeaderedContentControl hc: Add(hc.Header); break;
                case ContentControl cc when cc is not Window && cc.IsVisible: Add(cc.Content); break;
            }
            if (d is FrameworkElement fe && fe.IsVisible) Add(fe.ToolTip);
        }
        if (bad.Count > 0) Fail($"{where}: untranslated", string.Join(" | ", bad.Take(12)));
        else Pass($"{where}: texts translated");
        // Turkish letters in the English UI with no translation at all (user content is marked Skip and not checked)
        var turkish = new SortedSet<string>();
        foreach (var tb in Tree(w).OfType<TextBlock>())
            if (tb.IsVisible && !UiTranslator.GetSkip(tb) && !L.Has(tb.Text) && tb.Text.IndexOfAny("çğışöüÇĞİŞÖÜ".ToCharArray()) >= 0 && !IsInsideItemsControl(tb))
                turkish.Add(tb.Text.Length > 60 ? tb.Text[..60] + "…" : tb.Text);
        if (turkish.Count > 0) Warn($"{where}: Turkish text without translation", string.Join(" | ", turkish.Take(10)));
    }

    /// <summary>Clicks the window's Close / Cancel button like a user and expects the window to go away.</summary>
    async Task CheckCloseButton(Window w, string where)
    {
        var names = new[] { "Kapat", "Close", "Vazgeç", "Cancel", "İptal" }.SelectMany(n => new[] { n, L.T(n) }).ToHashSet();
        var button = Tree(w).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.IsEnabled && (b.IsCancel || names.Contains(TextOf(b.Content).Trim())));
        if (button == null) { Warn($"{where}: close button", "no Close/Cancel button (only the title bar X)"); w.Close(); return; }
        Invoke(button);
        if (await Until(() => !w.IsVisible, 3000)) Pass($"{where}: '{TextOf(button.Content)}' closes the window");
        else { Fail($"{where}: close button", $"'{TextOf(button.Content)}' did not close the window"); w.Close(); }
    }

    static void Invoke(Button b)
    {
        if (UIElementAutomationPeer.CreatePeerForElement(b) is IInvokeProvider p) p.Invoke();
        else b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>Screenshot + texts + airspace + close button of a window.</summary>
    async Task InspectWindow(Window w, string name, bool closeWithButton = true)
    {
        await Until(() => w.IsLoaded && w.IsVisible, 8000);
        await Wait(700);
        Check(w.IsVisible, $"{name}: opens");
        Shot(w, name);
        CheckTexts(w, name);
        CheckAirspace(w, name);
        if (closeWithButton) await CheckCloseButton(w, name);
    }

    /// <summary>Runs <paramref name="open"/> (which may block in ShowDialog) and inspects the window it opens.</summary>
    async Task OpenAndInspect(string name, Action open, Func<Window, Task>? inspect = null, bool closeWithButton = true)
    {
        var before = Application.Current.Windows.OfType<Window>().ToHashSet();
        async Task Inspector()
        {
            await Wait(50);
            if (!await Until(() => Application.Current.Windows.OfType<Window>().Any(x => !before.Contains(x) && x.IsVisible), 15000)) { Fail(name, "window did not open"); return; }
            var w = Application.Current.Windows.OfType<Window>().First(x => !before.Contains(x) && x.IsVisible);
            try { if (inspect != null) await inspect(w); else await InspectWindow(w, name, closeWithButton); }
            catch (Exception ex) { Fail(name, ex.ToString()); }
            if (w.IsVisible) { try { w.Close(); } catch { } }
        }
        var inspector = Inspector();
        open();
        await inspector;
    }

    void CloseStrayWindows(string step)
    {
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(x => x != this && x.IsVisible && x is not ReminderWindow).ToList())
        {
            Warn(step, $"window left open: {w.GetType().Name} '{w.Title}'");
            if (w is ComposeWindow c) ForceClose(c); else try { w.Close(); } catch { }
        }
        foreach (var c in OwnedWindows.OfType<ComposeWindow>().ToList()) ForceClose(c); // inline (hidden) composers too
    }

    // ---- message boxes: logged and answered (No / Cancel) from a background thread so nothing blocks
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    void StartDialogWatchdog()
    {
        var pid = (uint)Environment.ProcessId;
        new Thread(() =>
        {
            while (_stRunning)
            {
                Thread.Sleep(700);
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out var p);
                    if (p != pid || !IsWindowVisible(h)) return true;
                    var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                    if (cls.ToString() != "#32770") return true;
                    var title = new StringBuilder(256); GetWindowText(h, title, 256);
                    var text = new StringBuilder();
                    var buttons = new List<(IntPtr Handle, string Text)>();
                    EnumChildWindows(h, (c, _) =>
                    {
                        var cc = new StringBuilder(32); GetClassName(c, cc, 32);
                        var t = new StringBuilder(1024); GetWindowText(c, t, 1024);
                        if (cc.ToString() == "Static" && t.Length > 0) text.Append(t).Append(' ');
                        if (cc.ToString() == "Button") buttons.Add((c, t.ToString().Replace("&", "")));
                        return true;
                    }, IntPtr.Zero);
                    // Click like a user: No, else Cancel, else OK (Yes/No boxes ignore WM_CLOSE)
                    var order = new[] { "Hayır", "No", "İptal", "Cancel", "Vazgeç", "Tamam", "OK" };
                    var pick = order.Select(o => buttons.FirstOrDefault(b => b.Text.Equals(o, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(b => b.Handle != IntPtr.Zero);
                    if (pick.Handle == IntPtr.Zero && buttons.Count > 0) pick = buttons[^1];
                    Log("BOX", $"'{title}' {text.ToString().Trim().Replace("\n", " ")} → '{(pick.Handle != IntPtr.Zero ? pick.Text : "close")}'");
                    if (pick.Handle != IntPtr.Zero) PostMessage(pick.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
                    else PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
            }
        }) { IsBackground = true, Name = "selftest-dialogs" }.Start();
    }

    // ===================================================================== steps

    async Task StepMailList()
    {
        Check(await Until(() => MessageList.Items.Count >= 9, 20000), "demo mails listed", $"{MessageList.Items.Count} items");
        await Until(() => _webReady, 15000);
        Check(_webReady, "reading pane WebView2 ready");
        Shot(this, "mail-main");
        CheckTexts(this, "main window");
        CheckAirspace(this, "main window (mail)");
    }

    async Task StepEveryMessage()
    {
        var mails = MessageList.Items.OfType<MailHeader>().ToList();
        foreach (var m in mails)
        {
            MessageList.SelectedItem = m;
            var ok = await Until(() => _current == m && _currentContent != null, 8000);
            await Wait(500);
            Check(ok, $"mail {m.Uid} '{m.Subject}' opens in the reading pane");
            var invite = m.Uid == 9;
            Check(InviteBar.Visibility == (invite ? Visibility.Visible : Visibility.Collapsed), $"mail {m.Uid}: invitation bar {(invite ? "shown" : "hidden")}");
        }
        MessageList.SelectedItem = mails.FirstOrDefault(m => m.Uid == 1);
        await Wait(800);
        Shot(this, "mail-reading-pane");
    }

    async Task StepInvitation()
    {
        var m = MessageList.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == 9);
        if (m == null) { Fail("invitation", "demo invitation mail not found"); return; }
        MessageList.SelectedItem = m;
        await Until(() => _current == m && InviteBar.Visibility == Visibility.Visible, 8000);
        Shot(this, "mail-invitation");
        var accept = Tree(InviteBar).OfType<Button>().FirstOrDefault(b => TextOf(b.Content) == L.T("Kabul et"));
        Check(accept != null, "invitation: Accept button present");
        if (accept != null) { Invoke(accept); await Wait(2500); }
        Check(_calendar.FindByUid("demo-fai-review@contoso-aero.example") is { Response: MeetingResponse.Accepted }, "invitation: accepted meeting is in the calendar");
        CloseStrayWindows("invitation reply draft");
    }

    async Task StepAddressBook()
    {
        var link = Tree(FromPanel).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Hyperlink>()).FirstOrDefault();
        Check(link != null && link.ContextMenu != null, "sender is a clickable link with a menu");
        await OpenAndInspect("address book", () => OnAddressBook(this, new RoutedEventArgs()));
        await OpenAndInspect("address book (pick)", () => new ContactsWindow(pickMode: true) { Owner = this }.ShowDialog());
        await OpenAndInspect("contact edit", () => new ContactEditWindow(new Contact { FirstName = "Self", LastName = "Test", Email = "self.test@example.test" }) { Owner = this }.ShowDialog());
        await OpenAndInspect("contact group", () => new GroupEditWindow(new ContactGroup { Name = "Self-test" }) { Owner = this }.ShowDialog());
    }

    async Task StepCompose()
    {
        var m = MessageList.Items.OfType<MailHeader>().First(x => x.Uid == 1);
        MessageList.SelectedItem = m; await Until(() => _current == m && _currentContent != null, 8000);
        await OpenAndInspect("message window", () => OnMessageDoubleClick(this, null!));
        await OpenAndInspect("new mail", () => OnNewMail(this, new RoutedEventArgs()), async w =>
        {
            if (w is ComposeWindow c) await Until(() => c.IsEditorInitialized, 15000);
            await Wait(800); Shot(w, "new mail"); CheckTexts(w, "new mail"); CheckAirspace(w, "new mail");
        });
        await OpenAndInspect("missing attachment dialog", () => MissingAttachmentDialog.Show(this, "ektedir"));
        foreach (var c in OwnedWindows.OfType<ComposeWindow>().ToList()) ForceClose(c);
        InlineReplyCheck();
    }

    void InlineReplyCheck()
    {
        try
        {
            if (_current == null || _currentContent == null) return;
            Reply(_current, _currentContent, all: false);
            Check(InlineComposeHost.Content != null || OwnedWindows.OfType<ComposeWindow>().Any(), "reply opens (inline in the reading pane)");
            CheckAirspace(this, "inline reply");
            Shot(this, "inline-reply");
        }
        finally { foreach (var c in OwnedWindows.OfType<ComposeWindow>().ToList()) ForceClose(c); }
    }

    /// <summary>Closes a compose window answering "save draft?" with No (nothing is saved or sent).</summary>
    static void ForceClose(ComposeWindow c)
    {
        try { typeof(ComposeWindow).GetField("_confirmClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(c, (Func<MessageBoxResult>)(() => MessageBoxResult.No)); } catch { }
        try { c.Close(); } catch { }
    }

    async Task StepAttachments()
    {
        foreach (var uid in new uint[] { 1, 2, 3, 4, 5 })
        {
            var m = MessageList.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == uid);
            if (m == null) continue;
            MessageList.SelectedItem = m; await Until(() => _current == m, 8000); await Wait(600);
            foreach (var att in _cache.GetAttachments(m.AccountId, m.Folder, m.Uid).ToList())
            {
                var name = "attachment " + att.FileName;
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                var open = OpenAttachmentAsync(m, att);
                if (!await Until(() => Application.Current.Windows.OfType<Window>().Any(x => !before.Contains(x) && x.IsVisible), 30000)) { Fail(name, "no viewer window opened"); continue; }
                await Wait(2500); // let the viewer render (PDF, CAD, Office); Office files may switch to another window
                var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => !before.Contains(x) && x.IsVisible);
                if (w == null) { Fail(name, "viewer window closed by itself"); continue; }
                await InspectWindow(w, name, closeWithButton: false);
                foreach (var extra in Application.Current.Windows.OfType<Window>().Where(x => !before.Contains(x) && x.IsVisible).ToList()) extra.Close();
                await Until(() => open.IsCompleted, 3000);
            }
        }
    }

    async Task StepRevisionDiff()
    {
        SwitchModule(PimModule.Mail);
        var inbox = _roots.SelectMany(r => r.Children).FirstOrDefault(f => f.AccountId == DemoData.AccountId && f.SortOrder == 0);
        if (inbox != null) { SelectFolder(inbox); await Wait(500); }
        var all = _cache.GetHeaders(DemoData.AccountId, "INBOX", 200);
        MailHeader? MailWith(string file) => all.FirstOrDefault(h => _cache.GetAttachments(h.AccountId, h.Folder, h.Uid).Any(a => a.FileName == file));

        // 1. earlier revisions are found in the same correspondent's mails
        var revCMail = MailWith("flange-DN100-revC.pdf");
        Check(revCMail != null, "revision: demo mail with flange rev C");
        if (revCMail == null) return;
        var cands = RevisionService.FindCandidates(_cache, revCMail, "flange-DN100-revC.pdf");
        Check(cands.Count > 0 && cands[0].FileName == "flange-DN100-revB.pdf", $"revision: rev B offered first for rev C ({string.Join(", ", cands.Select(c => c.FileName))})");

        // 2. the chooser dialog: pick the proposed revision like a user
        var revCAtt = _cache.GetAttachments(revCMail.AccountId, revCMail.Folder, revCMail.Uid).First(a => a.FileName == "flange-DN100-revC.pdf");
        var newPath = await DownloadAttachmentAsync(revCMail, revCAtt);
        Check(newPath != null, "revision: rev C downloaded");
        if (newPath == null) return;
        var before = Application.Current.Windows.OfType<Window>().ToHashSet();
        var pickOp = Dispatcher.InvokeAsync(() => PickOlderRevisionAsync(this, newPath));
        var chooserOpen = await Until(() => Application.Current.Windows.OfType<Window>().Any(w => !before.Contains(w) && w.IsVisible), 8000);
        var chooser = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => !before.Contains(w) && w.IsVisible);
        Check(chooserOpen && chooser != null, "revision: chooser dialog opens");
        string? oldPath = null;
        if (chooser != null)
        {
            Shot(chooser, "revision-chooser"); CheckTexts(chooser, "revision chooser");
            var compare = Tree(chooser).OfType<Button>().FirstOrDefault(b => TextOf(b.Content) == L.T("Karşılaştır"));
            Check(compare != null, "revision chooser: Compare button");
            if (compare != null) Invoke(compare); else chooser.Close();
            oldPath = await await pickOp.Task;
        }
        Check(oldPath != null && Path.GetFileName(oldPath) == "flange-DN100-revB.pdf", $"revision: rev B chosen and downloaded ({Path.GetFileName(oldPath ?? "")})");

        // 3. every file type gives a real, specific difference
        async Task<RevisionDiffWindow?> Diff(string oldFile, string newFile, string label)
        {
            var oMail = MailWith(oldFile); var nMail = MailWith(newFile);
            if (oMail == null || nMail == null) { Fail(label, "demo files missing"); return null; }
            var o = await DownloadAttachmentAsync(oMail, _cache.GetAttachments(oMail.AccountId, oMail.Folder, oMail.Uid).First(a => a.FileName == oldFile));
            var n = await DownloadAttachmentAsync(nMail, _cache.GetAttachments(nMail.AccountId, nMail.Folder, nMail.Uid).First(a => a.FileName == newFile));
            var b = Application.Current.Windows.OfType<Window>().ToHashSet();
            await ShowRevisionDiffAsync(o!, n!, nMail);
            await Until(() => Application.Current.Windows.OfType<RevisionDiffWindow>().Any(w => !b.Contains(w) && w.IsVisible), 60000);
            var win = Application.Current.Windows.OfType<RevisionDiffWindow>().FirstOrDefault(w => !b.Contains(w) && w.IsVisible);
            Check(win != null, label + ": window opens");
            return win;
        }
        RevisionDiffReport? ReportOf(RevisionDiffWindow w) => typeof(RevisionDiffWindow).GetField("_report", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(w) as RevisionDiffReport;

        if (await Diff("delivery-schedule-wk43.xlsx", "delivery-schedule-wk44.xlsx", "revision Excel") is { } xw)
        {
            var r = ReportOf(xw)!;
            Check(r.Items.Any(i => i.Property.StartsWith("NW-1042") && i.OldValue == "120" && i.NewValue == "150"), "revision Excel: NW-1042 quantity 120 → 150");
            await InspectWindow(xw, "revision diff excel", closeWithButton: true);
        }
        if (await Diff("flange-DN100-revB.dxf", "flange-DN100-revC.dxf", "revision DXF") is { } dw)
        {
            var r = ReportOf(dw)!;
            Check(r.Items.Any(i => (i.NewValue ?? "").Contains("Ø20")), $"revision DXF: hole note Ø18 → Ø20 ({r.Items.Count} changes)");
            Check(r.Items.Any(i => (i.NewValue ?? "").Contains("REV C")), "revision DXF: title block REV B → C");
            await InspectWindow(dw, "revision diff dxf", closeWithButton: true);
        }
        if (await Diff("bracket-revB.stp", "bracket-revC.stp", "revision STEP") is { } sw)
        {
            var r = ReportOf(sw)!;
            Check(r.Items.Any(i => i.Category is "Delik" or "Delik / Silindir" or "Hacim" or "Kutu Ölçüleri"), $"revision STEP: measured geometry changes ({string.Join(" | ", r.Items.Select(i => i.Description).Take(4))})");
            await InspectWindow(sw, "revision diff step", closeWithButton: true);
        }
        if (oldPath != null)
        {
            var b = Application.Current.Windows.OfType<Window>().ToHashSet();
            await ShowRevisionDiffAsync(oldPath, newPath, revCMail);
            await Until(() => Application.Current.Windows.OfType<RevisionDiffWindow>().Any(w => !b.Contains(w) && w.IsVisible), 90000);
            if (Application.Current.Windows.OfType<RevisionDiffWindow>().FirstOrDefault(w => !b.Contains(w) && w.IsVisible) is { } pw)
            {
                var r = ReportOf(pw)!;
                Check(r.TotalChanges > 0 || r.Notes.Count > 0, $"revision PDF: result or an honest note ({r.TotalChanges} changes, notes: {string.Join(" ", r.Notes)})");
                await InspectWindow(pw, "revision diff pdf", closeWithButton: true);
            }
            else Fail("revision PDF", "window did not open");
        }
    }

    async Task StepCalendar()
    {
        SwitchModule(PimModule.Calendar);
        await Wait(800);
        Check(PimHost.Visibility == Visibility.Visible, "calendar module visible");
        Check(ReadingPane.Visibility != Visibility.Visible, "mail reading pane is hidden behind the calendar");
        CheckAirspace(this, "calendar");
        foreach (var mode in new[] { CalendarViewMode.Day, CalendarViewMode.WorkWeek, CalendarViewMode.Week, CalendarViewMode.Month })
        {
            _calendarView!.SetMode(mode); await Wait(600);
            var chips = Tree(_calendarView).OfType<Border>().Count(b => b.Tag is Occurrence);
            Check(chips > 0 || mode == CalendarViewMode.Day, $"calendar {mode}: items drawn ({chips})");
            Shot(this, "calendar-" + mode);
            CheckAirspace(this, "calendar " + mode);
        }
        CheckTexts(this, "calendar");
        SwitchModule(PimModule.Mail); await Wait(400);
        Check(ReadingPane.Visibility == Visibility.Visible && PimHost.Visibility != Visibility.Visible, "back to mail shows the reading pane again");
        SwitchModule(PimModule.Calendar); await Wait(300);
    }

    async Task StepEventForm()
    {
        if (_calendarView == null) SwitchModule(PimModule.Calendar);
        await OpenAndInspect("appointment form", () => _calendarView!.NewEvent(false), async w =>
        {
            await InspectWindow(w, "appointment form", closeWithButton: false);
            if (w is EventWindow ev)
            {
                ((TextBox)typeof(EventWindow).GetField("_title", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(ev)!).Text = "Self-test appointment";
                var save = Tree(ev).OfType<Button>().FirstOrDefault(b => TextOf(b.Content) == L.T("Kaydet ve Kapat"));
                Check(save != null, "appointment form: Save & Close button");
                if (save != null) { Invoke(save); Check(await Until(() => !ev.IsVisible, 3000), "appointment form: Save & Close closes"); }
            }
        });
        Check(_calendar.Events.Any(e => e.Title == "Self-test appointment"), "appointment saved to the calendar");
        await OpenAndInspect("meeting form", () => _calendarView!.NewEvent(true), async w => { await InspectWindow(w, "meeting form", closeWithButton: false); w.Close(); });
    }

    async Task StepTasks()
    {
        SwitchModule(PimModule.Tasks); await Wait(800);
        CheckAirspace(this, "tasks");
        var quick = Tree(_tasksView!).OfType<TextBox>().FirstOrDefault();
        if (quick != null)
        {
            quick.Focus(); quick.Text = "Self-test task " + L.T("yarın");
            quick.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(quick)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await Wait(500);
        }
        Check(_tasks.Tasks.Any(t => t.Title == "Self-test task" && t.DueDate == DateTime.Today.AddDays(1)), "tasks: quick add with 'tomorrow'");
        var taskList = Tree(_tasksView!).OfType<ListBox>().FirstOrDefault();
        var items = (taskList != null ? taskList.Items.OfType<ListBoxItem>() : Tree(_tasksView!).OfType<ListBoxItem>()).Where(i => i.Tag is TaskItem).ToList();
        Check(_tasks.Tasks.Count >= 5 && items.Count >= 1, $"tasks: list shows the tasks ({items.Count})");
        if (items.FirstOrDefault() is { } first) { first.IsSelected = true; await Wait(400); }
        Shot(this, "tasks");
        CheckTexts(this, "tasks");
        SwitchModule(PimModule.Mail); await Wait(300);
        var m = MessageList.Items.OfType<MailHeader>().First(x => x.Uid == 2);
        MessageList.SelectedItem = m; await Until(() => _current == m, 5000);
        OnMailToTask(this, new RoutedEventArgs());
        Check(_tasks.Tasks.Any(t => t.SourceMail == $"{m.AccountId}|{m.Folder}|{m.Uid}"), "mail → task");
    }

    async Task StepQuoteDesk()
    {
        SwitchModule(PimModule.Quotes); await Wait(800);
        Check(PimHost.Visibility == Visibility.Visible, "quote desk module visible");
        Check(ReadingPane.Visibility != Visibility.Visible, "mail reading pane hidden behind quote desk");
        CheckAirspace(this, "quotes");
        Check(_quoteDeskView != null, "quote desk view initialized");
        if (_quoteDeskView == null) return;
        Check(_quoteStore.Quotes.Count >= 3, $"quote desk has quotes ({_quoteStore.Quotes.Count})");
        Shot(this, "quotes-desk");
        CheckTexts(this, "quotes-desk");

        // search
        var all = _quoteDeskView.RowCount;
        _quoteDeskView.SearchText = "NW-1042"; await Wait(200);
        Check(_quoteDeskView.RowCount >= 1 && _quoteDeskView.RowCount < all, $"quote desk search narrows the list ({_quoteDeskView.RowCount}/{all})");
        _quoteDeskView.SearchText = ""; await Wait(200);

        // status change through the same command as the ribbon; saved and the selection kept
        var q = _quoteStore.Quotes.First(x => x.Status != QuoteStatus.Won);
        Check(_quoteDeskView.SelectQuote(q.Id), "quote desk: select a quote");
        _quoteDeskView.SetStatusOfSelected(QuoteStatus.Won); await Wait(200);
        Check(new QuoteStore().Quotes.FirstOrDefault(x => x.Id == q.Id)?.Status == QuoteStatus.Won, "quote desk: status saved to quotes.json");

        // Excel export
        var xlsx = Path.Combine(_stDir, "quotes-export.xlsx");
        QuoteStore.ExportToExcel(_quoteStore.Quotes, xlsx);
        using (var wb = new ClosedXML.Excel.XLWorkbook(xlsx))
            Check(wb.Worksheet(1).Cell(1, 2).GetString() == L.T("Parça No") && wb.Worksheet(1).RowsUsed().Count() == _quoteStore.Quotes.Count + 1, "quote desk: Excel export has a header row and every quote");

        // RFQ detection on the demo request mail
        var rfqMail = _cache.GetHeaders(DemoData.AccountId, "INBOX", 200).FirstOrDefault(h => h.Subject.Contains("120"));
        if (rfqMail != null)
        {
            var atts = _cache.GetAttachments(rfqMail.AccountId, rfqMail.Folder, rfqMail.Uid).Select(a => a.FileName).ToList();
            var (isRfq, found) = QuoteStore.DetectRfq(rfqMail.Subject, rfqMail.Preview, $"{rfqMail.FromName} <{rfqMail.FromAddress}>", atts);
            Check(isRfq && found.Quantity == 120, $"quote desk: demo request recognised (qty {found.Quantity}, part {found.PartNumber})");
        }

        var editDlg = new QuoteEditDialog(_quoteStore.Quotes.First(), isNew: false) { Owner = this };
        editDlg.Show(); await Wait(400);
        Shot(editDlg, "quote-edit-dialog");
        CheckTexts(editDlg, "quote-edit-dialog");
        editDlg.Close();

        SwitchModule(PimModule.Mail); await Wait(300);
    }

    async Task StepTemplatesAndQuickSteps()
    {
        var tmplWin = new TemplateManagerWindow(_templateStore, this);
        tmplWin.Show(); await Wait(400);
        Check(tmplWin.IsVisible, "template manager window opened");
        Shot(tmplWin, "templates-manager");
        CheckTexts(tmplWin, "templates-manager");
        tmplWin.Close();

        // variables are filled from the mail
        var tmpl = _templateStore.Templates.FirstOrDefault(t => t.Body.Contains("{Ad}") || t.Body.Contains("{FirstName}"));
        Check(tmpl != null, "templates: a template with a name variable exists");
        if (tmpl != null)
        {
            var text = TemplateEngine.Render(tmpl.Body, TemplateContext.Create(to: "\"Anna Keller\" <anna.keller@northwind-machining.example>", subject: "NW-1042 rev C", signature: "MailStudio", bodyForPartExtraction: "NW-1042"));
            Check(text.Contains("Anna") && !text.Contains("{Ad}") && !text.Contains("{FirstName}"), "templates: {Ad} filled with the recipient's first name");
        }

        var qsWin = new QuickStepManagerWindow(_quickStepStore, _templateStore, this);
        qsWin.Show(); await Wait(400);
        Check(qsWin.IsVisible, "quick steps manager window opened");
        Shot(qsWin, "quickstep-manager");
        CheckTexts(qsWin, "quickstep-manager");
        qsWin.Close();

        // a quick step really changes the mail
        var mail = _messages.FirstOrDefault(m => !m.IsFlagged);
        var flagStep = new QuickStepItem { Name = "Self-test flag", Actions = { new QuickStepAction { Type = QuickStepActionType.Flag }, new QuickStepAction { Type = QuickStepActionType.MarkRead } } };
        if (mail != null)
        {
            var executed = await ExecuteQuickStepAsync(flagStep, mail);
            var stored = _cache.GetHeaders(mail.AccountId, mail.Folder, 500).FirstOrDefault(h => h.Uid == mail.Uid);
            Check(executed && mail.IsFlagged && stored?.IsFlagged == true && stored.IsRead, "quick step: flag + read applied and saved");
            Shot(this, "quickstep-executed");
        }
    }

    async Task StepSnooze()
    {
        SwitchModule(PimModule.Mail); await Wait(300);
        var dlg = new SnoozeCustomDateDialog { Owner = this };
        dlg.Show(); await Wait(400);
        Check(dlg.IsVisible, "snooze dialog opened");
        Shot(dlg, "snooze-dialog");
        CheckTexts(dlg, "snooze-dialog");
        await CheckCloseButton(dlg, "snooze-dialog");

        var m = _messages.FirstOrDefault(x => x.Uid == 2) ?? _messages.FirstOrDefault();
        if (m == null) return;
        var realDate = m.Date;
        MessageList.SelectedItem = m; await Wait(300);
        SnoozeSelected(DateTimeOffset.Now.AddSeconds(2));
        await Wait(300);
        Check(!MessageList.Items.OfType<MailHeader>().Any(h => h.Uid == m.Uid), "snoozed mail hidden from the list");
        Shot(this, "snooze-mail-hidden");
        if (FilterSnoozed != null)
        {
            FilterSnoozed.IsChecked = true; await Wait(400);
            Check(MessageList.Items.OfType<MailHeader>().Any(h => h.Uid == m.Uid), "snoozed mail under the Snoozed filter");
            Shot(this, "snooze-filter-tab");
            FilterAll.IsChecked = true; await Wait(300);
        }
        await Wait(2500);
        CheckSnoozeDue(); await Wait(800);
        var back = MessageList.Items.OfType<MailHeader>().FirstOrDefault(h => h.Uid == m.Uid);
        Check(back != null, "snoozed mail comes back when its time is up");
        var stored = _cache.GetHeaders(m.AccountId, m.Folder, 500).FirstOrDefault(h => h.Uid == m.Uid);
        Check(stored != null && !stored.IsRead, "returned mail is unread");
        Check(stored != null && Math.Abs((stored.Date - realDate).TotalSeconds) < 1, "returned mail keeps its real date");
        Shot(this, "snooze-mail-restored");
    }

    async Task StepRules()
    {
        SwitchModule(PimModule.Mail);
        var acc = _store.Accounts.First();
        var inbox = _cache.GetHeaders(acc.Id, "INBOX", 200);
        var rule = new MailRule
        {
            Name = "Self-test: NW-1042 → category", Enabled = true, StopProcessing = false,
            Conditions = { new RuleCondition { Field = "subject", Value = "NW-1042" } },
            Actions = { new RuleAction { Type = "set_category", Value = "Self-test" }, new RuleAction { Type = "mark_as_flagged" } }
        };
        Check(RuleEngine.Validate(rule) == null, "rules: valid rule accepted");
        Check(RuleEngine.Validate(new MailRule { Name = "x", Conditions = { new RuleCondition { Field = "from", Value = "" } }, Actions = { new RuleAction { Type = "move_to_trash" } } }) != null, "rules: empty condition refused");
        var matching = inbox.Where(h => h.Subject.Contains("NW-1042")).ToList();
        _ruleStore.Rules.Add(rule); _ruleStore.Save();
        await RunRulesOnFreshAsync(acc, inbox);
        _ruleStore.Rules.Remove(rule); _ruleStore.Save();
        var after = _cache.GetHeaders(acc.Id, "INBOX", 200);
        Check(matching.Count > 0 && matching.All(m => after.First(h => h.Uid == m.Uid) is { IsFlagged: true, Category: "Self-test" }), $"rules: applied to the {matching.Count} matching new mails");
        Check(after.Where(h => !h.Subject.Contains("NW-1042")).All(h => h.Category != "Self-test"), "rules: other mails untouched");
    }

    async Task StepUndoSendAndOutbox()
    {
        var acc = _store.Accounts.First();
        // undo: the mail waits, nothing is sent, Undo brings the composer back
        QueueSend(acc, new ComposeRequest { To = "anna.keller@northwind-machining.example", Subject = "Self-test undo", Body = "x" }, 30);
        await Wait(300);
        Check(UndoSendBar.Visibility == Visibility.Visible, "undo send: bar shown");
        Shot(this, "undo-send-bar");
        CheckTexts(this, "undo send bar");
        var before = OwnedWindows.OfType<ComposeWindow>().ToHashSet();
        OnUndoSendClick(this, new RoutedEventArgs());
        Check(await Until(() => OwnedWindows.OfType<ComposeWindow>().Any(c => !before.Contains(c)), 8000), "undo send: the mail is back in a composer");
        foreach (var c in OwnedWindows.OfType<ComposeWindow>().Where(c => !before.Contains(c)).ToList()) ForceClose(c);
        Check(!_outbox.Items.Any(i => i.Request.Subject == "Self-test undo"), "undo send: removed from the outbox");

        // a send that fails after the undo window (demo mode never sends) gives the mail back, it is not lost
        QueueSend(acc, new ComposeRequest { To = "anna.keller@northwind-machining.example", Subject = "Self-test failed send", Body = "x" }, 1);
        before = OwnedWindows.OfType<ComposeWindow>().ToHashSet();
        Check(await Until(() => OwnedWindows.OfType<ComposeWindow>().Any(c => !before.Contains(c)), 10000), "undo send: a failed send reopens the composer");
        foreach (var c in OwnedWindows.OfType<ComposeWindow>().Where(c => !before.Contains(c)).ToList()) ForceClose(c);

        // send later: listed in the outbox window with its time; deleting it removes it
        ScheduleSend(acc, new ComposeRequest { To = "tom.becker@contoso-aero.example", Subject = "Self-test later", Body = "x" }, DateTimeOffset.Now.AddDays(1));
        Check(_outbox.Items.Any(i => i.Request.Subject == "Self-test later" && i.Status == OutboxStatus.Scheduled), "send later: queued");
        var w = new OutboxWindow(_outbox, (_, _) => { }, () => { }, this);
        w.Show(); await Wait(400);
        Shot(w, "outbox-window"); CheckTexts(w, "outbox window");
        Check(Tree(w).OfType<TextBlock>().Any(t => t.Text == "Self-test later"), "outbox window lists the scheduled mail");
        await CheckCloseButton(w, "outbox window");
        _outbox.Remove(_outbox.Items.First(i => i.Request.Subject == "Self-test later").Id);
        UpdateUndoBar();
    }

    async Task StepFollowUp()
    {
        var sent = new FollowUpItem
        {
            AccountId = DemoData.AccountId, MessageId = "selftest-followup@mailstudio.example", Subject = "Self-test follow-up",
            Recipients = "tom.becker@contoso-aero.example", SentDate = DateTimeOffset.Now.AddDays(-4), DueDays = 3, DueDate = DateTimeOffset.Now.AddDays(-1)
        };
        _followUpStore.Add(sent);
        var created = _followUpStore.CheckExpired(_tasks);
        var task = _tasks.Tasks.FirstOrDefault(t => t.FollowUpMessageId == sent.MessageId);
        Check(created >= 1 && task != null && task.Category == "Cevap bekleniyor", "follow-up: overdue → task in 'Waiting for reply'");
        SwitchModule(PimModule.Tasks); await Wait(600);
        Shot(this, "tasks-waiting-for-reply");
        CheckTexts(this, "tasks waiting for reply");
        if (task != null)
        {
            var before = OwnedWindows.OfType<ComposeWindow>().ToHashSet();
            ((ITaskHost)this).ComposeReminder(task);
            Check(await Until(() => OwnedWindows.OfType<ComposeWindow>().Any(c => !before.Contains(c)), 8000), "follow-up: reminder mail opens (not sent)");
            foreach (var c in OwnedWindows.OfType<ComposeWindow>().Where(c => !before.Contains(c)).ToList()) ForceClose(c);
            // the answer arrives → follow-up and task close by themselves
            var reply = new MailHeader { AccountId = DemoData.AccountId, Folder = "INBOX", Uid = 9999, FromAddress = "tom.becker@contoso-aero.example", Subject = "Re: Self-test follow-up", InReplyTo = "<selftest-followup@mailstudio.example>" };
            var resolved = _followUpStore.CheckReplies(new[] { reply }, _tasks);
            Check(resolved == 1 && _tasks.Find(task.Id)?.Completed == true, "follow-up: the reply closes it and completes the task");
        }
        SwitchModule(PimModule.Mail); await Wait(300);
    }

    async Task StepUnifiedInboxAndSearchFolders()
    {
        // 1. Unified inbox
        var unifiedNode = _roots.FirstOrDefault(r => r.IsUnifiedInbox);
        Check(unifiedNode != null, "unified inbox node exists in folder tree");
        if (unifiedNode != null)
        {
            SelectFolder(unifiedNode); await Wait(600);
            Check(MessageList.Items.Count >= 8, $"unified inbox lists demo mails ({MessageList.Items.Count})");
            CheckAirspace(this, "unified-inbox");
            Shot(this, "unified-inbox");
            CheckTexts(this, "unified-inbox");
        }

        // 2. Search folders root
        var searchRoot = _roots.FirstOrDefault(r => r.IsSearchFolderRoot);
        Check(searchRoot != null && searchRoot.Children.Count >= 3, "search folders root exists with default folders");

        // 3. Search folder: Unread
        var unreadFolder = searchRoot?.Children.FirstOrDefault(c => c.SearchFolderId == "search-unread");
        Check(unreadFolder != null, "search folder 'Okunmamış' found");
        if (unreadFolder != null)
        {
            var demoMails = _cache.GetAllHeaders().Take(2).ToList();
            foreach (var dm in demoMails)
                _cache.SetFlags(dm.AccountId, dm.Folder, dm.Uid, read: false);
            MessageList.SelectedItem = null;
            _current = null;
            SelectFolder(unreadFolder); await Wait(600);
            var unreadItems = MessageList.Items.OfType<MailHeader>().ToList();
            Check(unreadItems.Count >= 1 && unreadItems.All(h => !h.IsRead), $"search folder unread contains only unread mails ({unreadItems.Count})");
            Shot(this, "search-folder-unread");
            CheckFolderBadges("search-folder-unread");
        }

        // 4. Search folder: Flagged
        var flaggedFolder = searchRoot?.Children.FirstOrDefault(c => c.SearchFolderId == "search-flagged");
        Check(flaggedFolder != null, "search folder 'Bayraklı' found");
        if (flaggedFolder != null)
        {
            SelectFolder(flaggedFolder); await Wait(600);
            var flaggedItems = MessageList.Items.OfType<MailHeader>().ToList();
            Check(flaggedItems.Count >= 1 && flaggedItems.All(h => h.IsFlagged), $"search folder flagged contains only flagged mails ({flaggedItems.Count})");
            Shot(this, "search-folder-flagged");
        }

        // 5. Search folder: This Week's Attachments
        var attFolder = searchRoot?.Children.FirstOrDefault(c => c.SearchFolderId == "search-this-week-attachments");
        Check(attFolder != null, "search folder 'Bu haftanın ekli mailleri' found");
        if (attFolder != null)
        {
            SelectFolder(attFolder); await Wait(600);
            var attItems = MessageList.Items.OfType<MailHeader>().ToList();
            Check(attItems.Count >= 1 && attItems.All(h => h.HasAttachments), $"search folder attachments contains only mails with attachments ({attItems.Count})");
            Shot(this, "search-folder-attachments");
        }

        // 6. SearchFolderDialog UI
        var sfDlg = new Views.SearchFolders.SearchFolderDialog();
        sfDlg.Show(); await Wait(400);
        Check(sfDlg.IsVisible, "SearchFolderDialog opened successfully");
        Shot(sfDlg, "search-folder-dialog");
        CheckTexts(sfDlg, "search-folder-dialog");
        sfDlg.Close();

        // Restore to inbox
        if (unifiedNode != null) { SelectFolder(unifiedNode); await Wait(400); }
        Pass("unified inbox and search folders");
    }

    async Task StepNewslettersAndUnsubscribe()
    {
        // 1. Select the demo newsletter mail (Uid = 7, Northwind Newsletter)
        var newsletterMail = MessageList.Items.OfType<MailHeader>().FirstOrDefault(m => m.Uid == 7);
        Check(newsletterMail != null, "newsletter demo mail (Uid=7) found");
        if (newsletterMail != null)
        {
            MessageList.SelectedItem = newsletterMail;
            await Until(() => _current == newsletterMail && _currentContent != null, 8000);
            await Wait(600);

            // 2. UnsubscribeBar visibility and buttons
            Check(UnsubscribeBar.Visibility == Visibility.Visible, "UnsubscribeBar visible for newsletter mail");
            Check(UnsubscribeButton.IsVisible, "UnsubscribeButton visible");
            Check(MoveToNewslettersButton.IsVisible, "MoveToNewslettersButton visible");
            Shot(this, "newsletter-reading-pane");
            CheckAirspace(this, "newsletter reading pane");
            CheckTexts(this, "newsletter reading pane");

            // 3. Filter tab 'Bültenler'
            if (FilterNewsletters != null && FilterAll != null)
            {
                FilterNewsletters.IsChecked = true; await Wait(400);
                var filtered = MessageList.Items.OfType<MailHeader>().ToList();
                Check(filtered.Any(h => h.Uid == 7), "newsletter mail visible under Bültenler filter");
                Check(filtered.All(h => NewsletterHelper.IsNewsletter(h, _ruleStore)), "all mails under Bültenler filter are newsletters");
                Shot(this, "newsletter-filter-tab");

                // Switch back to 'Tümü'
                FilterAll.IsChecked = true; await Wait(300);
            }

            // 4. Test unsubscribe mail draft opening
            OpenUnsubscribeMailDraft("mailto:unsubscribe@northwind-machining.example?subject=unsubscribe");
            await Wait(500);
            var composeWin = OwnedWindows.OfType<ComposeWindow>().FirstOrDefault();
            Check(composeWin != null, "unsubscribe compose draft opened");
            if (composeWin != null)
            {
                var toBox = composeWin.FindName("ToBox") as Views.RecipientBox;
                Check(toBox != null && toBox.Text.Contains("unsubscribe@northwind-machining.example"), "compose To contains unsubscribe address");
                Shot(composeWin, "newsletter-unsubscribe-compose");
                CheckTexts(composeWin, "unsubscribe compose");
                ForceClose(composeWin);
            }

            // 5. Test 'Bu göndereni her zaman Bültenler klasörüne taşı'
            await AlwaysMoveSenderToNewslettersAsync(newsletterMail);
            await Wait(400);
            Check(_ruleStore.Rules.Any(r => r.Conditions.Any(c => c.Field == "from" && (c.Value ?? "").Contains("news@northwind-machining.example"))), "rule added to move sender to Newsletters folder");
        }
        Pass("newsletters and unsubscribe");
    }

    async Task StepCalendarSync()
    {
        var syncAcc = new AccountConfig { Id = "sync-demo-account", DisplayName = "Sync Demo", Email = "sync@example.com", AuthType = AuthType.Google, EnableCalendarSync = true };
        await OpenAndInspect("account settings (calendar sync)", () => new AccountWindow(syncAcc, _settings, _oauth) { Owner = this }.ShowDialog(), async w =>
        {
            await InspectWindow(w, "account-calendar-sync", closeWithButton: false);
            var chk = Tree(w).OfType<CheckBox>().FirstOrDefault(c => c.Name == "CalendarSyncCheck");
            Check(chk != null && chk.IsChecked == true, "account window: calendar sync checked");
            var notice = Tree(w).OfType<Border>().FirstOrDefault(b => b.Name == "CalendarConsentNotice");
            Check(notice != null && notice.Visibility == Visibility.Visible, "account window: separate calendar consent explained");
            w.Close();
        });

        SwitchModule(PimModule.Calendar); await Wait(800);
        Check(Tree(this).OfType<Button>().Any(b => TextOf(b.Content).Contains(L.T("Şimdi Eşitle"))), "calendar ribbon: Sync now");
        Shot(this, "calendar-sync-ribbon");

        // no account with sync on → a clear message, nothing is contacted
        var err = "";
        try { await SyncCalendarsAsync(); } catch (Exception ex) { err = ex.Message; }
        Check(err == L.T("Takvim eşitlemesi açık bir hesap yok. Hesap ayarlarında 'Takvim etkinliklerini eşitle' seçeneğini aç."), "calendar sync: off by default, says so");

        // turned on for an account without a CalDAV address: refused before any network use; the event form offers that calendar
        var demo = _store.Accounts.First();
        demo.EnableCalendarSync = true;
        try
        {
            err = "";
            try { await SyncCalendarsAsync(); } catch (Exception ex) { err = ex.Message; }
            Check(err.Contains("CalDAV"), $"calendar sync: no address → no request ({err})");
            await OpenAndInspect("appointment form with account calendar", () => _calendarView!.NewEvent(false), async w =>
            {
                var combo = Tree(w).OfType<ComboBox>().FirstOrDefault(c => c.Items.OfType<ComboBoxItem>().Any(i => (i.Tag as string) == demo.Id));
                Check(combo != null, "event form: the synced account calendar can be chosen");
                Shot(w, "event-form-calendar-choice");
                w.Close();
                await Task.CompletedTask;
            }, closeWithButton: false);
        }
        finally { demo.EnableCalendarSync = false; }
        SwitchModule(PimModule.Mail); await Wait(300);
    }

    async Task StepSmallPolish()
    {
        // 1. Contact Card Window from reading pane sender link
        await OpenAndInspect("contact card", () => OpenContactCard("Anna Keller", "anna.keller@northwind-machining.example"), async w =>
        {
            await InspectWindow(w, "contact-card", closeWithButton: false);
            var closeBtn = Tree(w).OfType<Button>().FirstOrDefault(b => TextOf(b.Content) == L.T("Kapat") || b.IsCancel);
            Check(closeBtn != null, "contact card: close button present");
            if (closeBtn != null) Invoke(closeBtn);
        });

        // 2. ComposeWindow with large attachment warning (>20MB) and ZIP compression
        await OpenAndInspect("compose large attachment", () => OnNewMail(this, new RoutedEventArgs()), async w =>
        {
            if (w is ComposeWindow c)
            {
                await Until(() => c.IsEditorInitialized, 15000);
                var largeFile = Path.Combine(Path.GetTempPath(), "selftest-large-" + Guid.NewGuid().ToString("N") + ".dat");
                using (var fs = new FileStream(largeFile, FileMode.Create, FileAccess.Write))
                {
                    fs.SetLength(22 * 1024 * 1024);
                }
                c.AddAttachmentFiles(new[] { largeFile });
                await Wait(400);

                var warningBorder = c.FindName("LargeAttachmentWarning") as Border;
                Check(warningBorder != null && warningBorder.IsVisible, "large attachment warning banner is visible");
                Shot(c, "compose-large-attachment-warning");
                CheckTexts(c, "compose-large-attachment-warning");

                // Click ZIP button
                var zipBtn = c.FindName("ZipAttachmentsBtn") as Button;
                Check(zipBtn != null, "compose: zip attachments button exists");
                if (zipBtn != null) Invoke(zipBtn);
                await Wait(500);

                Check(warningBorder == null || !warningBorder.IsVisible, "large attachment warning collapsed after ZIP");

                // 3. @ mention in compose window
                await c.InsertMentionAsync("Anna Keller", "anna.keller@northwind-machining.example");
                await Wait(400);
                Shot(c, "compose-at-mention");
                CheckTexts(c, "compose-at-mention");

                try { if (File.Exists(largeFile)) File.Delete(largeFile); } catch { }
                ForceClose(c);
            }
        }, closeWithButton: false);

        Pass("small polish");
    }

    async Task StepReminders()
    {
        _calendar.Save(new CalendarEvent { Title = "Self-test reminder", Start = DateTime.Now.AddMinutes(3), End = DateTime.Now.AddMinutes(30), ReminderMinutes = 10 });
        PimTick(); await Wait(800);
        var w = _reminderWindow;
        Check(w is { IsVisible: true }, "reminder window opens");
        if (w == null) return;
        Shot(w, "reminders"); CheckTexts(w, "reminders");
        var dismissAll = Tree(w).OfType<Button>().FirstOrDefault(b => TextOf(b.Content) == L.T("Tümünü kapat"));
        if (dismissAll != null) { Invoke(dismissAll); Check(await Until(() => !w.IsVisible, 3000), "reminders: Dismiss all closes the window"); }
        else Fail("reminders", "Dismiss all button missing");
    }

    async Task StepOtherWindows()
    {
        var acc = _store.Accounts.First();
        await OpenAndInspect("account settings", () => new AccountWindow(acc, _settings, _oauth) { Owner = this }.ShowDialog());
        await OpenAndInspect("signatures", () => new SignatureManagerWindow(this, acc).ShowDialog());
        await OpenAndInspect("conditional formatting", () => OnFormatRules(this, new RoutedEventArgs()));
        await OpenAndInspect("email rules", () => OnRules(this, new RoutedEventArgs()));
        await OpenAndInspect("skills", () => OnSkills(this, new RoutedEventArgs()));
        await OpenAndInspect("agents", () => OnAgents(this, new RoutedEventArgs()));
    }

    async Task StepDarkTheme()
    {
        var old = ThemeManager.IsDark;
        ThemeManager.Apply(ThemeManager.Parse("Dark")); await Wait(1000);
        Shot(this, "dark-mail");
        SwitchModule(PimModule.Calendar); await Wait(700); Shot(this, "dark-calendar");
        SwitchModule(PimModule.Tasks); await Wait(700); Shot(this, "dark-tasks");
        SwitchModule(PimModule.Quotes); await Wait(700); Shot(this, "dark-quotes");
        SwitchModule(PimModule.Mail);
        ThemeManager.Apply(ThemeManager.Parse(old ? "Dark" : "Light")); await Wait(500);
        Pass("theme switch");
    }
}

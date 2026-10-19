using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailStudio;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Views;
using MailStudio.Services.Calendar;
using MailStudio.Views.Calendar;
using MailStudio.Regression;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    static int _passed;
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    static object? Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    static void Check(bool value, string name) { if (!value) throw new Exception(name); _passed++; Console.WriteLine("PASS " + name); }
    static async Task Until(Func<bool> ready, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("WPF readiness timeout"); await Task.Delay(50); }
    }
    static void Close(ComposeWindow w) { Set(w,"_closingConfirmed",true); w.Close(); }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandChild in Descendants(child)) yield return grandChild;
        }
    }
    // English UI audit: any visible text that is still a Turkish source string from the dictionary is a failure;
    // other Turkish-looking texts (usually synthetic mail data) are listed for review.
    static readonly SortedSet<string> Untranslated = new(StringComparer.Ordinal);
    static readonly SortedSet<string> TurkishLooking = new(StringComparer.Ordinal);
    static readonly System.Text.RegularExpressions.Regex TurkishChars = new("[çğıöşüÇĞİÖŞÜ]");
    static void Audit(Window window, string name, bool closing = false)
    {
        if (!L.IsEnglish || (!closing && !window.IsLoaded)) return;
        var texts = new List<string>();
        void Add(object? o) { if (o is string s && s.Trim().Length > 0) texts.Add(s); }
        Add(window.Title);
        foreach (var d in Descendants(window).Prepend(window))
        {
            switch (d)
            {
                case TextBlock tb: Add(tb.Text); break;
                case HeaderedItemsControl hi: Add(hi.Header); break;
                case HeaderedContentControl hc: Add(hc.Header); Add(hc.Content); break;
                case ContentControl cc when cc is not Window: Add(cc.Content); break;
            }
            if (d is FrameworkElement fe) Add(fe.ToolTip);
            if (d is DataGrid dg) foreach (var c in dg.Columns) Add(c.Header);
            if (d is ComboBox cb) foreach (var it in cb.Items) Add(it);
        }
        foreach (var t in texts.Distinct())
        {
            if (L.Has(t) && L.T(t) != t) Untranslated.Add($"[{name}] {t}");
            else if (TurkishChars.IsMatch(t)) TurkishLooking.Add($"[{name}] {(t.Length > 90 ? t[..90] : t)}");
        }
    }
    static void Capture(Window window,string name)
    {
        Audit(window, name);
        window.UpdateLayout();var element=(FrameworkElement)window.Content;var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(element);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(AppPaths.Root,name));encoder.Save(file);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var data = Path.Combine(Path.GetTempPath(), "MailStudio-ui-regression-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MAILSTUDIO_DATA_DIR", data);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // 1.0: themes and control styles come from the Themes/ dictionaries in the MailStudio assembly (same as App.xaml).
        app.Resources = new ResourceDictionary();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MailStudio;component/Themes/Light.xaml", UriKind.Absolute) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MailStudio;component/Themes/Controls.xaml", UriKind.Absolute) });
        MailStudio.Services.ThemeManager.Apply(MailStudio.Services.ThemeMode.Light);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var result = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(args); result=0; }
            catch(Exception ex) { Console.WriteLine("FAIL " + ex);foreach(var pending in app.Windows.OfType<ComposeWindow>())Console.WriteLine("EDITOR STATUS "+((TextBlock)pending.FindName("StatusText")).Text+" / ready="+pending.IsEditorInitialized); }
            finally
            {
                foreach(var w in app.Windows.OfType<ComposeWindow>().ToList()) Close(w);
                app.Shutdown(); app.Dispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run(); Console.Out.Flush(); Environment.Exit(result); return result;
    }

    static async Task RunAsync(string[] args)
    {
        await using var imap = new FakeImap(); await using var smtp = new FakeSmtp { DataDelay=TimeSpan.FromMilliseconds(400) };
        if (L.IsEnglish && Environment.GetEnvironmentVariable("MAILSTUDIO_AUDIT_TIMER") != "0")
        {
            // Audit every window in its final state when it closes (and the open ones explicitly at the end).
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) try { Audit(w, w.GetType().Name, true); } catch { } }));
        }
        var account = new AccountConfig { Id="ui-test", Email="owner@example.test", ImapHost="127.0.0.1", ImapPort=imap.Port, ImapSecurity=SecurityMode.None, SmtpHost="127.0.0.1", SmtpPort=smtp.Port, SmtpSecurity=SecurityMode.None };
        var cache = new MailCache(); cache.Init();
        using var mail = new MailService(cache,new OAuthService(()=>new AppSettings()));
        var readingWeb = new WebView2();
        var readingWindow = new Window { Content=readingWeb,Width=680,Height=220,Title="Synthetic encoding check" };
        readingWindow.Show();
        await readingWeb.EnsureCoreWebView2Async(await MailHtml.EnvAsync());
        using(var mimeStream=new MemoryStream(System.Text.Encoding.ASCII.GetBytes(EncodingFixtures.Message("iso-8859-3",true))))
        using(var mime=MimeKit.MimeMessage.Load(mimeStream))
        {
            var loaded=new TaskCompletionSource();
            readingWeb.CoreWebView2.NavigationCompleted+=(_,_)=>loaded.TrySetResult();
            MailHtml.Show(readingWeb,MailService.BuildContent(mime).Html);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var rendered=JsonSerializer.Deserialize<string>(await readingWeb.CoreWebView2.ExecuteScriptAsync("document.body.innerText"))!;
            Check(rendered.Contains(EncodingFixtures.Text) && rendered.Contains("ACME© 2026; 10¹, º"),"real reading WebView preserves legacy Turkish text and genuine symbols");
            Check(await readingWeb.CoreWebView2.ExecuteScriptAsync("document.characterSet") == "\"UTF-8\"","decoded mail is displayed as UTF-8 rather than decoded a second time");
        }
        // The real application keeps its reading WebView alive while standalone
        // composers open/close. Retain the same environment anchor in this host;
        // disposing the last view between fixtures races browser-process exit.
        readingWindow.Closed+=(_,_)=>readingWeb.Dispose();readingWindow.Hide();
        var compose = new ComposeWindow(new[]{account},account.Id,new(){To="recipient@example.test",Subject="Synthetic UI regression",Body="Hello"},mail,(_,_)=>Task.FromResult("AI test"),()=>"Test");
        compose.Show(); await Until(()=>compose.IsEditorInitialized);
        var editor = (WebView2)compose.FindName("Editor");
        Check(await editor.CoreWebView2.ExecuteScriptAsync("typeof ms.snapshot") == "\"function\"", "real WebView2 editor initializes under CSP");
        var state=Field<ComposeState>(compose,"_state"); state.Saved(state.Revision);
        ((RecipientBox)compose.FindName("ToBox")).Text="new@example.test";
        Check(state.IsDirty,"recipient-only change is tracked"); state.Saved(state.Revision);
        ((RecipientBox)compose.FindName("BccBox")).Text="hidden@example.test";
        Check(state.IsDirty,"BCC-only change is tracked"); state.Saved(state.Revision);
        ((TextBox)compose.FindName("SubjectBox")).Text="Changed subject";
        Check(state.IsDirty,"subject-only change is tracked"); state.Saved(state.Revision);
        var attachments=Field<System.Collections.ObjectModel.ObservableCollection<string>>(compose,"_attachments"); attachments.Add("synthetic");
        Check(state.IsDirty,"attachment add is tracked"); state.Saved(state.Revision); attachments.Remove("synthetic");
        Check(state.IsDirty,"attachment removal is tracked");
        var attack="<b>Safe quote</b><img src='http://127.0.0.1:1/track' onerror='window.injected=true'><script>window.injected=true</script><div id='ms-draft'>bad ID</div>";
        await editor.CoreWebView2.ExecuteScriptAsync($"chrome.webview.postMessage(JSON.stringify({{t:'paste',html:{JsonSerializer.Serialize(attack)}}}))");
        await Task.Delay(250);
        var scriptResult=await editor.CoreWebView2.ExecuteScriptAsync("JSON.stringify({ran:!!window.injected,shell:document.querySelectorAll('#ms-draft').length,script:!!document.querySelector('#ms-root script'),event:!!document.querySelector('#ms-root [onerror]')})");
        Check(scriptResult.Contains("\\\"ran\\\":false") && scriptResult.Contains("\\\"shell\\\":1") && scriptResult.Contains("\\\"event\\\":false"),"quoted/pasted HTML cannot execute events or replace editor shell");
        var dropDir=Path.Combine(AppPaths.Root,"drop-fixtures");Directory.CreateDirectory(dropDir);
        var dropFiles=new[]{Path.Combine(dropDir,"synthetic.pdf"),Path.Combine(dropDir,"synthetic.txt")};
        foreach(var file in dropFiles) File.WriteAllText(file,"Synthetic attachment");
        await editor.CoreWebView2.ExecuteScriptAsync("{let i=document.createElement('input');i.type='file';i.multiple=true;i.id='drop-fixture-input';document.body.appendChild(i);}");
        using(var dom=JsonDocument.Parse(await editor.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.getDocument","{}")))
        {
            using var node=JsonDocument.Parse(await editor.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.querySelector",JsonSerializer.Serialize(new{nodeId=dom.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32(),selector="#drop-fixture-input"})));
            await editor.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",JsonSerializer.Serialize(new{nodeId=node.RootElement.GetProperty("nodeId").GetInt32(),files=dropFiles}));
        }
        var dropScript="{const dt=new DataTransfer();for(const f of document.getElementById('drop-fixture-input').files)dt.items.add(f);document.getElementById('ms-root').dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:dt}));}";
        await editor.CoreWebView2.ExecuteScriptAsync(dropScript);await Until(()=>attachments.Count==2);
        Check(attachments.SequenceEqual(dropFiles),"browser file drop reaches WPF attachments through native File objects");
        await editor.CoreWebView2.ExecuteScriptAsync(dropScript);await Task.Delay(100);
        Check(attachments.Count==2 && state.IsDirty,"repeated file drop deduplicates attachments and tracks changes");
        attachments.Clear();
        Invoke(compose,"OnSend",compose,new RoutedEventArgs());
        Invoke(compose,"OnSend",compose,new RoutedEventArgs());
        Check(state.IsBusy && !((Button)compose.FindName("SendButton")).IsEnabled,"send gate is taken before first await");
        compose.Close(); Check(compose.IsVisible,"closing during sending is blocked");
        await Until(()=>!compose.IsVisible);
        Check(smtp.Accepted==1,"repeated send entry creates one SMTP message");

        // Isolated MainWindow fixture: remove application startup handler before showing it, so no accounts are loaded or synchronized.
        var main = new MainWindow(enableTray:false);
        main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler),main,main.GetType().GetMethod("OnLoaded",Private)!);
        Field<MailCache>(main,"_cache").Init(); Field<AccountStore>(main,"_store").Accounts.Add(account);
        main.Show();
        var a=new MailHeader { AccountId=account.Id,Folder="INBOX",Uid=2,UidValidity=77,MessageId="A@example.test",FromAddress="a@example.test",Subject="Message A",Date=DateTimeOffset.Now };
        var b=new MailHeader { AccountId=account.Id,Folder="INBOX",Uid=3,UidValidity=77,MessageId="B@example.test",FromAddress="b@example.test",Subject="Message B",Date=DateTimeOffset.Now };
        Set(main,"_current",a);Set(main,"_currentContent",new MailContent {Html="<p>Body A</p>",Text="Body A"});
        Invoke(main,"OnMessageDoubleClick",main,null);
        var message = main.OwnedWindows.OfType<MessageWindow>().Single();
        Set(main,"_current",b);Set(main,"_currentContent",new MailContent {Html="<p>Body B</p>",Text="Body B"});
        var toolbar=((Grid)message.Content).Children.OfType<WrapPanel>().First();
        ((Button)toolbar.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reply=main.OwnedWindows.OfType<ComposeWindow>().Single(); await Until(()=>reply.IsEditorInitialized);
        Check(reply.IsInline&&!reply.IsVisible&&((ContentControl)main.FindName("InlineComposeHost")).Content!=null,"reply is hosted inside the reading pane instead of opening a window");
        Check(((RecipientBox)reply.FindName("ToBox")).Text.Contains("a@example.test") && ((TextBox)reply.FindName("SubjectBox")).Text=="RE: Message A","separate Message A window replies to A after main selection changes to B");
        var replyEditor=(WebView2)reply.FindName("Editor");await replyEditor.CoreWebView2.ExecuteScriptAsync("ms.setDraft('<p>25 adet için 20MM pay.</p>');ms.setSignature('<b>Test imzası</b>')");
        ((Button)reply.FindName("PopOutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(()=>main.OwnedWindows.OfType<ComposeWindow>().Any(w=>w!=reply&&w.IsVisible));
        var popped=main.OwnedWindows.OfType<ComposeWindow>().Single();await Until(()=>popped.IsEditorInitialized);
        var poppedSnapshot=await popped.SnapshotAsync();Check(!popped.IsInline&&poppedSnapshot.EditableHtml!.Contains("20MM")&&poppedSnapshot.SignatureHtml!.Contains("Test imzası")&&poppedSnapshot.QuotedHtml!.Contains("Body A"),"pop out preserves edited body, signature and original quoted mail");Close(popped);

        Set(main,"_current",a);Set(main,"_currentContent",new MailContent {Html="<p>Body A</p>"});Invoke(main,"OnMessageDoubleClick",main,null);
        var forwardMessage=main.OwnedWindows.OfType<MessageWindow>().Single();Set(main,"_current",b);Set(main,"_currentContent",new MailContent {Html="<p>Body B</p>"});
        toolbar=((Grid)forwardMessage.Content).Children.OfType<WrapPanel>().First();((Button)toolbar.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var forward=main.OwnedWindows.OfType<ComposeWindow>().Single();await Until(()=>forward.IsEditorInitialized);
        Check(((TextBox)forward.FindName("SubjectBox")).Text=="FW: Message A" && Field<ComposeRequest>(forward,"_req").QuotedHtml!.Contains("Body A"),"separate Message A window forwards A after main selection changes to B");
        Close(forward);

        Set(main,"_current",a);Set(main,"_currentContent",new MailContent {Html="<p>Body A</p>"});Invoke(main,"OnMessageDoubleClick",main,null);
        var message2=main.OwnedWindows.OfType<MessageWindow>().Single();Set(main,"_current",b);Set(main,"_currentContent",new MailContent {Html="<p>Body B</p>"});
        toolbar=((Grid)message2.Content).Children.OfType<WrapPanel>().First();((Button)toolbar.Children[3]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(()=>!message2.IsVisible);
        Check(imap.Commands.Any(c=>c.StartsWith("UID MOVE 2 ")) && !imap.Commands.Any(c=>c.StartsWith("UID MOVE 3 ")),"separate Message A window deletes A after main selection changes to B");

        // ---- Calendar and tasks modules (synthetic items only)
        {
            var today = DateTime.Today;
            var cal = main.CalendarStore;
            var single = new CalendarEvent { Title = "Synthetic review", Start = today.AddHours(10), End = today.AddHours(11), Category = "Mavi" };
            var series = new CalendarEvent { Title = "Synthetic stand-up", Start = today.AddDays(-7).AddHours(8.5), End = today.AddDays(-7).AddHours(8.75), Repeat = new RepeatRule { Frequency = RepeatFrequency.Daily } };
            var fair = new CalendarEvent { Title = "Synthetic fair", Start = today.AddDays(1), End = today.AddDays(3), AllDay = true, Category = "Mor" };
            cal.Save(single); cal.Save(series); cal.Save(fair);
            main.SwitchModule(PimModule.Calendar); await Task.Delay(500);
            var view = main.CalendarViewForTests!;
            Check(((ContentControl)main.FindName("PimHost")).Visibility == Visibility.Visible && ((Grid)main.FindName("RibbonGrid")).Children.Contains(view.Ribbon) &&
                  ((UIElement)main.FindName("RibbonHome")).Visibility == Visibility.Collapsed && ((UIElement)main.FindName("FolderTree")).Visibility == Visibility.Collapsed,
                  "calendar module replaces the mail view, folder pane and ribbon");
            int Chips() => Descendants(view).OfType<Border>().Count(b => b.Tag is Occurrence);
            foreach (var mode in new[] { CalendarViewMode.Day, CalendarViewMode.WorkWeek, CalendarViewMode.Week, CalendarViewMode.Month })
            {
                view.SetMode(mode); main.UpdateLayout(); await Task.Delay(250);
                Check(Chips() >= (mode == CalendarViewMode.Day ? 2 : 3), $"calendar {mode} view shows the items ({Chips()})");
                if (mode == CalendarViewMode.Week) { Audit(main, "MainWindow(calendar)"); Capture(main, "calendar-week.png"); }
                if (mode == CalendarViewMode.Month) Capture(main, "calendar-month.png");
            }
            view.SetMode(CalendarViewMode.Day); main.UpdateLayout(); await Task.Delay(250);
            var block = Descendants(view).OfType<Border>().First(b => b.Tag is Occurrence o && o.Event.Id == single.Id);
            Check(Math.Abs(Canvas.GetTop(block) - (10 * CalendarView.HourHeight + 1)) < 0.5, "timed item is placed at its hour");
            Invoke(view, "ApplyMove", new Occurrence(single, single.Start, single.End), single.Start.AddHours(2), single.End.AddHours(2));
            Check(cal.Find(single.Id)!.Start == today.AddHours(12), "drag move updates the appointment");
            Invoke(view, "ApplyMove", new Occurrence(series, today.AddHours(8.5), today.AddHours(8.75)), today.AddHours(9), today.AddHours(9.25));
            Check(cal.Find(series.Id)!.Exceptions.Contains(today.AddHours(8.5)) && cal.Events.Any(e => e.Uid.StartsWith(series.Uid + "#") && e.Start == today.AddHours(9) && e.Repeat == null),
                  "moving one occurrence detaches it from the series");

            var draft = new CalendarEvent { Start = today.AddHours(12.25), End = today.AddHours(12.75) };
            var form = new EventWindow(draft, cal, isNew: true, meeting: false) { Owner = main };
            var conflictShown = false;
            form.Loaded += (_, _) => form.Dispatcher.BeginInvoke(() =>
            {
                Field<TextBox>(form, "_title").Text = "Dialog event";
                conflictShown = Field<TextBlock>(form, "_conflict").Visibility == Visibility.Visible;
                Field<ComboBox>(form, "_repeat").SelectedIndex = 1;
                Audit(form, "EventWindow");
                Invoke(form, "Finish", false);
            }, DispatcherPriority.Background);
            Check(form.ShowDialog() == true && draft.Title == "Dialog event" && draft.Repeat?.Frequency == RepeatFrequency.Daily && conflictShown, "event form saves title and repeat and warns about the conflict");

            var invite = new CalendarEvent { Uid = "ui-invite@example.test", Title = "Synthetic invite", Start = today.AddDays(2).AddHours(15), End = today.AddDays(2).AddHours(16), Attendees = { "owner@example.test" } };
            var inviteContent = new MailContent { Html = "<p>Invite</p>", Text = "Invite", CalendarIcs = Ics.WriteCalendar(new[] { invite }, "REQUEST") };
            Set(main, "_current", a); Invoke(main, "RenderBody", a, inviteContent);
            var bar = (Border)main.FindName("InviteBar");
            Check(bar.Visibility == Visibility.Visible && Descendants(bar).OfType<Button>().Any(x => (string)x.Content == L.T("Kabul et")), "meeting invitation shows Accept / Tentative / Decline in the reading pane");
            Invoke(main, "RespondInvitation", a, inviteContent, Ics.Parse(inviteContent.CalendarIcs), MeetingResponse.Accepted, "ui-test|INBOX|2");
            Check(cal.FindByUid("ui-invite@example.test") is { Response: MeetingResponse.Accepted, SourceMail: "ui-test|INBOX|2" }, "accepting adds the meeting to the calendar");

            main.SwitchModule(PimModule.Tasks); await Task.Delay(400);
            var tasksView = main.TasksViewForTests!;
            Invoke(tasksView, "QuickAdd", "Synthetic quote " + L.T("yarın"));
            Check(main.TaskStore.Tasks.Any(t => t.Title == "Synthetic quote" && t.DueDate == today.AddDays(1)), "quick add understands 'tomorrow'");
            Set(main, "_current", a); Invoke(main, "OnMailToTask", main, new RoutedEventArgs());
            Check(main.TaskStore.Tasks.Any(t => t.SourceMail == "ui-test|INBOX|2" && t.DueDate == today), "mail becomes a task due today");
            Invoke(main, "OnChatToTasks", new Button { Tag = new ChatMessage { Text = "- [ ] Synthetic AI task one\n2. Synthetic AI task two" } }, new RoutedEventArgs());
            Check(main.TaskStore.Tasks.Count(t => t.Title.StartsWith("Synthetic AI task")) == 2, "AI answer lines become tasks");
            main.UpdateLayout(); await Task.Delay(300);
            Check(Descendants(tasksView).OfType<ListBoxItem>().Count(i => i.Tag is TaskItem) >= 4, "task list shows the tasks");
            Audit(main, "MainWindow(tasks)"); Capture(main, "tasks.png");

            cal.Save(new CalendarEvent { Title = "Synthetic reminder", Start = DateTime.Now.AddMinutes(5), End = DateTime.Now.AddMinutes(35), ReminderMinutes = 15 });
            Invoke(main, "PimTick"); await Task.Delay(300);
            var reminders = Field<ReminderWindow?>(main, "_reminderWindow");
            Check(reminders is { IsVisible: true }, "reminder window opens for a due reminder");
            Audit(reminders!, "ReminderWindow"); Capture(reminders!, "reminders.png");
            Invoke(reminders!, "Dismiss", true);
            Check(!reminders!.IsVisible && cal.DueReminders(DateTime.Now).Count == 0, "dismiss all closes the reminders");
            main.SwitchModule(PimModule.Mail);
            Check(((ContentControl)main.FindName("PimHost")).Visibility == Visibility.Collapsed && ((UIElement)main.FindName("RibbonHome")).Visibility == Visibility.Visible, "back to mail restores the ribbon");
            Console.WriteLine("PASS calendar, tasks, invitations and reminders"); _passed++;
        }
        main.Close();
        var recovery=new ComposeWindow(new[]{account},account.Id,new(){Subject="Uncertain recovery",SubmissionId=Guid.NewGuid().ToString("N"),SubmissionState="Uncertain"},mail,(_,_)=>Task.FromResult(""),()=>"Test");
        recovery.Show();await Until(()=>recovery.IsEditorInitialized);
        Check(!((Button)recovery.FindName("SendButton")).IsEnabled,"reopened uncertain submission cannot be resent");Close(recovery);
        var noPrompts=0;
        var discard=new ComposeWindow(new[]{account},account.Id,new(){Subject="Discard regression"},mail,(_,_)=>Task.FromResult(""),()=>"Test",()=>{noPrompts++;return MessageBoxResult.No;});
        discard.Show();await Until(()=>discard.IsEditorInitialized);
        discard.Close();await Until(()=>!discard.IsVisible);
        Check(noPrompts==1 && Field<bool>(discard,"_closed"),"discard closes after the Closing event returns without reentrant Close");
        var savePrompts=0;
        var saving=new ComposeWindow(new[]{account},account.Id,new(){Subject="Save-on-close regression"},mail,(_,_)=>Task.FromResult(""),()=>"Test",()=>{savePrompts++;return MessageBoxResult.Yes;});
        saving.Show();await Until(()=>saving.IsEditorInitialized);saving.Close();await Until(()=>!saving.IsVisible);
        Check(savePrompts==1,"save-on-close persists draft then closes once");
        var delayedAi=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aiWindow=new ComposeWindow(new[]{account},account.Id,new(){Subject="Late AI regression",Body="draft"},mail,(_,_)=>delayedAi.Task,()=>"Test",()=>MessageBoxResult.No);
        aiWindow.Show();await Until(()=>aiWindow.IsEditorInitialized);
        var aiTask=(Task)Invoke(aiWindow,"RunAiAsync","test",true)!;await Until(()=>Field<CancellationTokenSource?>(aiWindow,"_aiCts")!=null);
        aiWindow.Close();await Until(()=>!aiWindow.IsVisible);delayedAi.SetResult("Late completion");await aiTask;
        Check(Field<bool>(aiWindow,"_closed"),"late AI completion does not show or update a closed editor");
        var early=new ComposeWindow(new[]{account},account.Id,new(),mail,(_,_)=>Task.FromResult(""),()=>"Test");early.Show();early.Close();await Task.Delay(100);
        Check(Field<bool>(early,"_closed") && !Field<DispatcherTimer>(early,"_autosave").IsEnabled,"closing during editor initialization does not restart or show it");
        account.Signatures=new(){new(){Id="s1",Name="İş",Html="<b>İş imzası</b>"},new(){Id="s2",Name="Kısa",Html="<i>Kısa imza</i>"}};account.DefaultSignatureId="s1";
        var signatureOwner=new Window();signatureOwner.Show();var signatureManager=new SignatureManagerWindow(signatureOwner,account);signatureManager.Show();await Until(()=>Field<bool>(signatureManager,"_ready"));
        var signatureEditor=Field<WebView2>(signatureManager,"_editor");await signatureEditor.CoreWebView2.ExecuteScriptAsync("ms.setDraft('<b>Düzenlenmiş imza</b>')");Field<TextBox>(signatureManager,"_name").Text="Düzenlenen";await (Task)Invoke(signatureManager,"SaveCurrentAsync")!;
        Check(signatureManager.Signatures[0].Name=="Düzenlenen"&&signatureManager.Signatures[0].Html.Contains("Düzenlenmiş imza")&&account.Signatures[0].Html=="<b>İş imzası</b>","signature editor changes remain local until explicitly saved");signatureManager.Close();
        var signatureEarly=new SignatureManagerWindow(signatureOwner,account);signatureEarly.Show();signatureEarly.Close();await Task.Delay(100);Check(!signatureEarly.IsVisible,"closing signature manager during initialization does not restart it");signatureOwner.Close();
        var firstProvider=new FakeComposeProvider("First");var secondProvider=new FakeComposeProvider("Second");
        var editing=new ComposeWindow(new[]{account},account.Id,new(){To="recipient@example.test",Body="25 adet için 20MM pay ve 3 eksende 20 dakika.",QuotedHtml="<p>Original quoted instruction must stay outside rewrite</p>"},mail,(_,_)=>Task.FromResult("unused"),()=>"First",()=>MessageBoxResult.No,new(new MailStudio.Services.Ai.IAiProvider[]{firstProvider,secondProvider},"First","model-a"));
        editing.Show();await Until(()=>editing.IsEditorInitialized);var editingWeb=(WebView2)editing.FindName("Editor");
        Check((await editing.SnapshotAsync()).SignatureHtml!.Contains("İş imzası"),"default signature is inserted into its own editable section on replies");
        ((ComboBox)editing.FindName("ComposeProviderBox")).SelectedItem="Second";await Until(()=>((ComboBox)editing.FindName("ComposeModelBox")).Items.Count==2);((ComboBox)editing.FindName("ComposeModelBox")).Text="model-b";
        var rewrite=(Task)Invoke(editing,"RunAiAsync","Taslaktaki yazım hatalarını düzelt.",true)!;
        await Until(()=>Application.Current.Windows.OfType<RewritePreviewWindow>().Any());var rewritePreview=Application.Current.Windows.OfType<RewritePreviewWindow>().Single();
        var before=await editing.SnapshotAsync();Check(before.EditableHtml!.Contains("20MM")&&secondProvider.LastModel=="model-b"&&!secondProvider.LastPrompt!.Contains("Original quoted instruction"),"compose provider/model is used and correction excludes the quoted conversation");
        rewritePreview.DialogResult=false;await rewrite;
        Check((await editing.SnapshotAsync()).EditableHtml==before.EditableHtml,"declining AI preview leaves original formatted draft unchanged");
        secondProvider.Response="25 adet için 20MM pay ve 3 eksende 30 dakika.";
        rewrite=(Task)Invoke(editing,"RunAiAsync","Taslaktaki yazım hatalarını düzelt.",true)!;await Until(()=>Application.Current.Windows.OfType<RewritePreviewWindow>().Any());rewritePreview=Application.Current.Windows.OfType<RewritePreviewWindow>().Single();
        var applyButton=Descendants(rewritePreview).OfType<Button>().Single(b=>b.Content is string text&&(text==L.T("Uygula")||text==L.T("Uygulanamaz")));
        Check(!applyButton.IsEnabled,"correction cannot apply an AI suggestion that changes machining time");rewritePreview.DialogResult=false;await rewrite;
        secondProvider.Response="25 adet için 20MM pay; 3 eksende 20 dakika.";
        rewrite=(Task)Invoke(editing,"RunAiAsync","Taslaktaki yazım hatalarını düzelt.",true)!;await Until(()=>Application.Current.Windows.OfType<RewritePreviewWindow>().Any());Application.Current.Windows.OfType<RewritePreviewWindow>().Single().DialogResult=true;await rewrite;
        var after=await editing.SnapshotAsync();Check(after.EditableHtml!.Contains("pay;")&&after.SignatureHtml==before.SignatureHtml&&after.QuotedHtml==before.QuotedHtml,"accepted correction only updates draft and preserves signature plus quote");
        await editingWeb.CoreWebView2.ExecuteScriptAsync("document.getElementById('ms-signature').innerHTML='<b>Elle düzenlenen imza</b>'");
        var signatureSnapshot=await editing.SnapshotAsync();Check(signatureSnapshot.SignatureHtml!.Contains("Elle düzenlenen imza")&&signatureSnapshot.AiModel=="model-b"&&signatureSnapshot.AiProvider=="Second","manual signature edits and compose model survive draft snapshots");
        // Close any open context menu before another test window is shown.
        await editingWeb.CoreWebView2.ExecuteScriptAsync("ms.focusStart()");Close(editing);
        await using(var photo=new FakeImageHttp())
        {
            var pictureMail=new ComposeWindow(new[]{account},account.Id,new(){BodyHtml=$"<p>Picture test</p><img src='{photo.Url}'>"},mail,(_,_)=>Task.FromResult(""),()=>"Test",()=>MessageBoxResult.No);
            pictureMail.Show();await Until(()=>pictureMail.IsEditorInitialized);await Task.Delay(150);Check(photo.Requests==0,"remote signature/quote pictures stay blocked until the user loads them");
            Invoke(pictureMail,"OnLoadComposeImages",pictureMail,new RoutedEventArgs());await Until(()=>photo.Requests>0);var pictureWeb=(WebView2)pictureMail.FindName("Editor");
            var loadedPicture=false;for(int i=0;i<40&&!loadedPicture;i++){loadedPicture=await pictureWeb.CoreWebView2.ExecuteScriptAsync("document.querySelector('img').naturalWidth>0")=="true";if(!loadedPicture)await Task.Delay(50);}
            Check(loadedPicture,"load images action renders the remote picture inside the compose editor");Close(pictureMail);
        }

        if(Environment.GetEnvironmentVariable("MAILSTUDIO_TECHNICAL_FIXTURES") is {} technicalFixtures)
        {
            TechnicalWorker.DevelopmentPython=Environment.GetEnvironmentVariable("MAILSTUDIO_TECHNICAL_PYTHON");TechnicalWorker.DevelopmentScript=Environment.GetEnvironmentVariable("MAILSTUDIO_TECHNICAL_SCRIPT");
            var workerExe=Environment.GetEnvironmentVariable("MAILSTUDIO_TECHNICAL_EXE");
            var stepWindow=new StepAttachmentWindow(Path.Combine(technicalFixtures,"box-ap242.stp"),workerExe);stepWindow.Show();await Until(()=>Field<bool>(stepWindow,"_loaded"),40);
            MailStudio.Services.AppSettings.Shared.LastMaterial="Al 7075-T6 / T7351";
            var massText=Field<TextBlock>(stepWindow,"_massInfo").Text;Check(massText.StartsWith(L.IsEnglish?"Part: 67":"Parça: 67")&&massText.Contains("24 cm³"),"raw material shows the native part weight (20×30×40 mm Al 7075 = 67.4 g)");
            Field<ComboBox>(stepWindow,"_material").SelectedItem=StepAttachmentWindow.Materials.First(m=>m.Name.StartsWith("C45"));Check(Field<TextBlock>(stepWindow,"_massInfo").Text.StartsWith(L.IsEnglish?"Part: 188":"Parça: 188"),"changing material updates part weight live (steel 188.4 g)");
            Invoke(stepWindow,"SetStockVolume",(double?)48000d);var stockText=Field<TextBlock>(stepWindow,"_massInfo").Text;Check((stockText.Contains("Stok: 376")||stockText.Contains("Stock: 376"))&&(stockText.Contains("%50")||stockText.Contains("50%")),"stock weight and utilisation update when the stock changes: "+stockText);
            Field<ComboBox>(stepWindow,"_material").SelectedItem=StepAttachmentWindow.Materials[0];
            Invoke(stepWindow,"Orbit",0d,300d);var up=Field<System.Windows.Media.Media3D.Vector3D>(stepWindow,"_up");Check(up.Z<0,"orbit is unlimited: rotating 135° over the top flips the camera instead of clamping");Invoke(stepWindow,"Orbit",0d,-300d);
            await (Task)Invoke(stepWindow,"SelectEntityAsync","face",1,new System.Windows.Media.Media3D.Point3D(0,15,20))!;await (Task)Invoke(stepWindow,"LookAtSelectedFaceAsync")!;
            var eye=Field<System.Windows.Media.Media3D.Vector3D>(stepWindow,"_eye");Check(Math.Max(Math.Abs(eye.X),Math.Max(Math.Abs(eye.Y),Math.Abs(eye.Z)))>.999,"perpendicular view aligns the camera with the selected planar face normal");
            Invoke(stepWindow,"ClearSelection");Invoke(stepWindow,"View",.7d,.5d);
            Check(Field<System.Windows.Media.Media3D.Model3DGroup>(stepWindow,"_model").Children.Count==6&&Field<ListBox>(stepWindow,"_pmi").Items.Count>=2,"native WPF CAD viewport shows six real CAD faces and semantic PMI");
            Check(Field<System.Windows.Media.Media3D.MeshGeometry3D>(stepWindow,"_pmiLines").Positions.Count>24,"WPF CAD viewport renders file-owned graphical PMI curves and glyphs");
            var pmiToggle=Field<CheckBox>(stepWindow,"_showPmi");pmiToggle.IsChecked=false;Check(Field<System.Windows.Media.Media3D.ModelVisual3D>(stepWindow,"_pmiVisual").Content==null,"graphical PMI can be hidden without changing CAD geometry");pmiToggle.IsChecked=true;
            Check(Field<System.Windows.Media.Media3D.Model3DGroup>(stepWindow,"_edges").Children.Count==12,"CAD viewport displays twelve B-Rep edges without triangle diagonals");
            int CountSelection() => ((System.Collections.ICollection)Field<object>(stepWindow,"_selection")).Count;
            async Task PickCad(double x,double y,double z){var screen=(Point)Invoke(stepWindow,"Screen",new System.Windows.Media.Media3D.Point3D(x,y,z))!;await (Task)Invoke(stepWindow,"PickAsync",screen)!;}
            JsonElement Response(Window w)=>Field<JsonElement?>(w,"_measureResponse")!.Value;
            double PairValue(Window w,string key)=>Response(w).GetProperty("results").EnumerateArray().Last(r=>r.GetProperty("indices").GetArrayLength()==2).GetProperty("rows").EnumerateArray().First(r=>r.GetProperty("key").GetString()==key).GetProperty("value").GetDouble();
            Field<ComboBox>(stepWindow,"_mode").SelectedIndex=0;Invoke(stepWindow,"View",0d,0d);await PickCad(20,15,20);Check(CountSelection()==1,"viewport mouse ray selects a real CAD face into the ordered object list");await PickCad(20,15,20);Check(CountSelection()==0,"clicking a selected CAD face again removes its selection");
            await PickCad(20,15,20);Field<ComboBox>(stepWindow,"_mode").SelectedIndex=1;Check(CountSelection()==1,"changing face-to-edge selection filter preserves the existing object list");await PickCad(20,0,20);
            Check(CountSelection()==2&&Field<ListBox>(stepWindow,"_objectList").Items.Count==2&&Field<Canvas>(stepWindow,"_sceneCanvas").Children.Count==1,"mixed face and edge picks infer measurements and show an actual scene dialog");Invoke(stepWindow,"ClearSelection");Field<ComboBox>(stepWindow,"_mode").SelectedIndex=0;
            await PickCad(20,15,20);await (Task)Invoke(stepWindow,"PickAsync",new Point(2,2))!;Check(CountSelection()==0,"clicking empty viewport space cancels active selection");
            await PickCad(20,15,20);stepWindow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(stepWindow)!,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});Check(CountSelection()==0,"Escape clears the active object list");
            var quickPoint=(Point)Invoke(stepWindow,"Screen",new System.Windows.Media.Media3D.Point3D(20,15,20))!;await (Task)Invoke(stepWindow,"ShowQuickPickAsync",quickPoint)!;var menu=Field<ContextMenu>(stepWindow,"_quickPickMenu");Check(menu.Items.OfType<MenuItem>().Any(i=>(i.Header.ToString()!.StartsWith("Gövde")||i.Header.ToString()!.StartsWith("Body")))&&menu.Items.OfType<MenuItem>().Any(i=>i.Header.ToString()!.StartsWith("Yüz")||i.Header.ToString()!.StartsWith("Face")),"right-click quick pick offers actual face body and component choices at the clicked geometry");menu.IsOpen=false;
            Field<ComboBox>(stepWindow,"_mode").SelectedIndex=1;await PickCad(20,0,20);Check(Field<DataGrid>(stepWindow,"_resultGrid").Items.Cast<object>().Any(r=>r.ToString()!.Contains(L.T("Uzunluk"))),"a single native edge infers length without choosing a measurement type first");Invoke(stepWindow,"ClearSelection");
            Field<ComboBox>(stepWindow,"_mode").SelectedIndex=2;await PickCad(20,8,10);await PickCad(20,14,18);
            Check(Math.Abs(PairValue(stepWindow,"minimum")-10)<1e-6&&Field<Canvas>(stepWindow,"_labelCanvas").Children.Count==1,"two native face points produce a 10 mm dimension and a scene result");
            var labels=(System.Collections.IList)Field<object>(stepWindow,"_labels");var firstLabel=((ValueTuple<System.Windows.Media.Media3D.Point3D,TextBlock>)labels[0]!).Item2;double labelX=Canvas.GetLeft(firstLabel);Invoke(stepWindow,"MoveDimensionLabel",firstLabel,new Vector(60,-25));Check(Math.Abs(Canvas.GetLeft(firstLabel)-labelX)>20&&Field<Canvas>(stepWindow,"_dimensionCanvas").Children.Count>=12,"moving a dimension label relocates it with witness and dimension lines");
            var filters=Field<Dictionary<string,CheckBox>>(stepWindow,"_resultFilters");filters["distance"].IsChecked=false;Check(Field<DataGrid>(stepWindow,"_resultGrid").Items.Count==0&&Field<Canvas>(stepWindow,"_sceneCanvas").Children.Count==0,"distance result filter affects both scene dialog and result table");filters["distance"].IsChecked=true;
            Field<ComboBox>(stepWindow,"_units").SelectedIndex=1;Check(Field<TextBox>(stepWindow,"_result").Text.Contains("in"),"measurement unit change updates the scene/table without reselecting objects");Field<ComboBox>(stepWindow,"_units").SelectedIndex=0;
            Invoke(stepWindow,"PinMeasurement");Check(CountSelection()==0&&Field<ListBox>(stepWindow,"_savedList").Items.Count==1&&Field<Canvas>(stepWindow,"_savedCanvas").Children.Count>0,"pinning retains the actual dimension while starting a new selection");
            await PickCad(20,0,0);Check(Field<DataGrid>(stepWindow,"_resultGrid").Items.Count==0,"point coordinates remain hidden when the coordinate filter is off");filters["coordinates"].IsChecked=true;Check(Field<DataGrid>(stepWindow,"_resultGrid").Items.Count==3,"coordinate filter exposes the single point XYZ values");filters["coordinates"].IsChecked=false;Invoke(stepWindow,"ClearSelection");
            await PickCad(20,8,10);await PickCad(20,14,18);await (Task)Invoke(stepWindow,"AddMeasureVectorAsync",new double[]{0,0,1})!;
            Check(Field<ListBox>(stepWindow,"_objectList").Items.Count==3&&Math.Abs(PairValue(stepWindow,"projected")-8)<1e-6,"vector is an explicit third list entry and projects the selected CAD points");
            Field<ComboBox>(stepWindow,"_distanceChoice").SelectedIndex=2;Check(Field<TextBox>(stepWindow,"_result").Text.Contains(L.T("Vektörde mesafe (noktalar)")),"scene distance variant changes from minimum to projection without new geometry picks");
            Capture(stepWindow,"step-measure-preview.png");Invoke(stepWindow,"ClearSelection");
            Set(stepWindow,"_directionPick",true);Field<ComboBox>(stepWindow,"_directionMode").SelectedIndex=0;await PickCad(20,15,20);Check(Field<TextBox>(stepWindow,"_direction").Text=="1, 0, 0","stock direction still comes from the actual CAD face normal");
            Field<TextBox>(stepWindow,"_stockOffset").Text="2";await (Task)Invoke(stepWindow,"EnvelopeAsync",false)!;Invoke(stepWindow,"ClearSelection");
            Check(Field<Canvas>(stepWindow,"_stockLabelCanvas").Children.Count==3&&Field<Canvas>(stepWindow,"_savedCanvas").Children.Count>0,"clearing active Measure preserves stock dimensions and pinned measurements independently");
            var edgeToggle=Field<CheckBox>(stepWindow,"_showEdges");edgeToggle.IsChecked=false;Check(Field<System.Windows.Media.Media3D.ModelVisual3D>(stepWindow,"_edgeVisual").Content==null,"CAD edge display toggles independently");edgeToggle.IsChecked=true;stepWindow.Close();
            var assemblyWindow=new StepAttachmentWindow(Path.Combine(technicalFixtures,"assembly-ap242.stp"),workerExe);assemblyWindow.Show();await Until(()=>Field<bool>(assemblyWindow,"_loaded"),40);
            Check(Field<Dictionary<int,JsonElement>>(assemblyWindow,"_bodyRecords").Count==2&&((TreeViewItem)Field<TreeView>(assemblyWindow,"_assemblyTree").Items[0]).Items.Count==2,"assembly navigator preserves separate component occurrences");
            Field<ComboBox>(assemblyWindow,"_mode").SelectedIndex=3;await (Task)Invoke(assemblyWindow,"SelectEntityAsync","body",1,new System.Windows.Media.Media3D.Point3D(10,15,20))!;await (Task)Invoke(assemblyWindow,"SelectEntityAsync","body",2,new System.Windows.Media.Media3D.Point3D(110,15,20))!;
            Check(Math.Abs(PairValue(assemblyWindow,"minimum")-80)<1e-6,"ordered body objects infer their native 80 mm separation");
            Field<ComboBox>(assemblyWindow,"_method").SelectedIndex=1;await Until(()=>Response(assemblyWindow).GetProperty("method").GetString()=="individual");Check(Response(assemblyWindow).GetProperty("results").GetArrayLength()==2,"method control recomputes individual body properties from the same selection");
            Invoke(assemblyWindow,"ClearSelection");Field<ComboBox>(assemblyWindow,"_function").SelectedIndex=1;await (Task)Invoke(assemblyWindow,"SelectEntityAsync","body",1,new System.Windows.Media.Media3D.Point3D(10,15,20))!;await (Task)Invoke(assemblyWindow,"SelectEntityAsync","body",2,new System.Windows.Media.Media3D.Point3D(110,15,20))!;
            Check(Field<ListBox>(assemblyWindow,"_objectList").Items.Count==1&&Math.Abs(Response(assemblyWindow).GetProperty("results")[0].GetProperty("rows")[0].GetProperty("value").GetDouble()-48000)<1e-4,"Object Set accumulates multiple bodies as one list object with summed native volume");
            Invoke(assemblyWindow,"IsolateBodies",new int[]{1});Check(Field<System.Windows.Media.Media3D.Model3DGroup>(assemblyWindow,"_model").Children.Count==6,"isolating a component hides other body faces");
            Field<TabControl>(assemblyWindow,"_tabs").SelectedIndex=1;Field<ComboBox>(assemblyWindow,"_stockScope").SelectedIndex=1;await (Task)Invoke(assemblyWindow,"EnvelopeAsync",false)!;
            double[] StockSizes()=>Field<JsonElement?>(assemblyWindow,"_lastStockResponse")!.Value.GetProperty("box_size").EnumerateArray().Select(v=>v.GetDouble()).ToArray();bool Sizes(double x,double y,double z)=>StockSizes().Zip(new[]{x,y,z}).All(pair=>Math.Abs(pair.First-pair.Second)<1e-4);
            Check(Sizes(20,30,40)&&Field<Canvas>(assemblyWindow,"_stockLabelCanvas").Children.Count==3,"selected visible component keeps its three stock dimension labels");
            Field<TextBox>(assemblyWindow,"_stockOffset").Text="2";await Until(()=>Sizes(24,34,44),20);Check(Sizes(24,34,44),"global offset still automatically refreshes all stock dimensions");
            await (Task)Invoke(assemblyWindow,"ApplyStockAsync")!;Field<CheckBox>(assemblyWindow,"_autoPreview").IsChecked=false;Field<TextBox>(assemblyWindow,"_stockOffset").Text="5";await (Task)Invoke(assemblyWindow,"EnvelopeAsync",false)!;await (Task)Invoke(assemblyWindow,"CancelStockAsync")!;Check(Sizes(24,34,44),"cancel restores last applied stock parameters");assemblyWindow.Close();
            var cylinderWindow=new StepAttachmentWindow(Path.Combine(technicalFixtures,"cylinder.stp"),workerExe);cylinderWindow.Show();await Until(()=>Field<bool>(cylinderWindow,"_loaded"),40);Invoke(cylinderWindow,"View",0d,Math.PI/2);Field<ComboBox>(cylinderWindow,"_mode").SelectedIndex=1;var cylinderPoint=(Point)Invoke(cylinderWindow,"Screen",new System.Windows.Media.Media3D.Point3D(5,0,50))!;await (Task)Invoke(cylinderWindow,"PickAsync",cylinderPoint)!;
            Check(Field<TextBox>(cylinderWindow,"_result").Text.Contains(L.T("Yarıçap"))&&Field<TextBox>(cylinderWindow,"_result").Text.Contains(L.T("Çap"))&&Field<Canvas>(cylinderWindow,"_sceneCanvas").Children.Count==1,"real circular edge pick infers radius and diameter in the scene dialog");
            Capture(cylinderWindow,"step-radius-preview.png");Invoke(cylinderWindow,"ClearSelection");Field<ComboBox>(cylinderWindow,"_function").SelectedIndex=2;Field<ComboBox>(cylinderWindow,"_pointConstruction").SelectedIndex=1;await (Task)Invoke(cylinderWindow,"PickAsync",cylinderPoint)!;
            Check(Response(cylinderWindow).GetProperty("results")[0].GetProperty("anchor")[2].GetDouble()>49,"Point center construction selects the native circle center at its actual Z plane");cylinderWindow.Close();
            if(Environment.GetEnvironmentVariable("MAILSTUDIO_REAL_CAD_TEST_PATH") is {} realCad&&File.Exists(realCad))
            {
                var realWindow=new StepAttachmentWindow(realCad,workerExe);realWindow.Show();await Until(()=>Field<bool>(realWindow,"_loaded"),60);Invoke(realWindow,"IsolateBodies",new int[]{1});Invoke(realWindow,"View",Math.PI+.65d,.35d);Field<ComboBox>(realWindow,"_mode").SelectedIndex=0;
                await (Task)Invoke(realWindow,"SelectEntityAsync","face",32,new System.Windows.Media.Media3D.Point3D(-37,-120,100))!;await (Task)Invoke(realWindow,"SelectEntityAsync","face",102,new System.Windows.Media.Media3D.Point3D(-62,-120,100))!;
                Check(Math.Abs(PairValue(realWindow,"minimum")-25)<1e-4&&Field<Canvas>(realWindow,"_sceneCanvas").Children.Count==1,"user assembly plate faces yield actual 25 mm CAD separation in a live scene result");realWindow.UpdateLayout();Invoke(realWindow,"UpdateLabels");var scene=Field<Border>(realWindow,"_sceneDialog");var sceneRect=new Rect(Canvas.GetLeft(scene),Canvas.GetTop(scene),scene.DesiredSize.Width,scene.DesiredSize.Height);Check(!Field<Canvas>(realWindow,"_labelCanvas").Children.OfType<TextBlock>().Any(l=>sceneRect.IntersectsWith(new Rect(Canvas.GetLeft(l),Canvas.GetTop(l),l.DesiredSize.Width,l.DesiredSize.Height))),"scene result dialog avoids covering the user's actual dimension labels");Capture(realWindow,"step-real-measure.png");
                Invoke(realWindow,"PinMeasurement");var kept=Field<Canvas>(realWindow,"_savedCanvas").Children.OfType<TextBlock>().ToArray();Check(!kept.SelectMany((a,i)=>kept.Skip(i+1).Select(b=>(a,b))).Any(pair=>new Rect(Canvas.GetLeft(pair.a),Canvas.GetTop(pair.a),pair.a.DesiredSize.Width,pair.a.DesiredSize.Height).IntersectsWith(new Rect(Canvas.GetLeft(pair.b),Canvas.GetTop(pair.b),pair.b.DesiredSize.Width,pair.b.DesiredSize.Height))),"pinning preserves the automatically separated dimension and angle label placement");await (Task)Invoke(realWindow,"SelectEntityAsync","edge",2,new System.Windows.Media.Media3D.Point3D(-62,-62.7218352053,235.499939117))!;
                Check(Field<TextBox>(realWindow,"_result").Text.Contains(L.T("Çap"))&&Field<ListBox>(realWindow,"_savedList").Items.Count==1,"user assembly circle shows native diameter while its previous face measurement remains pinned");Capture(realWindow,"step-real-radius.png");realWindow.Close();
            }
            var pdfWindow=new PdfAttachmentWindow(Path.Combine(technicalFixtures,"native.pdf"),new(new MailStudio.Services.Ai.IAiProvider[]{firstProvider},"First","model-a"),workerExe);pdfWindow.Show();await Until(()=>(bool)Invoke(pdfWindow,"TestPageRendered",0)!&&Field<TextBox>(pdfWindow,"_text").Text.Length>0,40);
            Check(Field<TextBox>(pdfWindow,"_text").Text.Contains("25 PCS"),"PDF window renders the page and exposes its actual text layer");
            var asking=(Task)Invoke(pdfWindow,"AskAsync")!;await asking;
            Check((firstProvider.LastPrompt!.Contains("PDF sayfa 1/1")||firstProvider.LastPrompt!.Contains("PDF page 1/1"))&&firstProvider.LastPrompt.Contains("25 PCS")&&Field<TextBox>(pdfWindow,"_answer").Text.Length>0,"PDF AI request contains the open page and source page reference");pdfWindow.Close();
            if(Environment.GetEnvironmentVariable("MAILSTUDIO_PDF_STUDIO_SAMPLE") is {} studioSample&&File.Exists(studioSample))
            {
                var studio=new PdfAttachmentWindow(studioSample,new(new MailStudio.Services.Ai.IAiProvider[]{firstProvider},"First","model-a"),workerExe);studio.Show();
                await Until(()=>(bool)Invoke(studio,"TestPageRendered",0)!&&Field<TextBox>(studio,"_text").Text.Contains("Flanş"),40);
                Check(Field<TextBox>(studio,"_text").Text.Contains("Ø120 h7"),"PDF studio reads the Unicode text layer as words");
                var z0=(double)studio.GetType().GetProperty("TestZoom",Private)!.GetValue(studio)!;Invoke(studio,"StepZoom",1,null);var z1=(double)studio.GetType().GetProperty("TestZoom",Private)!.GetValue(studio)!;Check(z1>z0,"zoom in increases page scale");
                await Until(()=>(bool)Invoke(studio,"TestPageRendered",0)!,20);
                var wa=(int)Invoke(studio,"TestWordIndex",0,"Malzeme:")!;var wb=(int)Invoke(studio,"TestWordIndex",0,"T7351")!;Check(wa>=0&&wb>wa,"word boxes are available for selection");
                Invoke(studio,"TestSelect",0,wa,wb);var picked=(string)studio.GetType().GetProperty("SelectedText")!.GetValue(studio)!;Check(picked=="Malzeme: Al 7075-T6 / T7351","selected words join to the original line text: '"+picked+"'");
                Invoke(studio,"TestTool","Highlight");Invoke(studio,"TestFinish",0);Check((int)studio.GetType().GetProperty("TestEditCount",Private)!.GetValue(studio)! ==1,"highlight from text selection adds one markup");
                var c=(int)Invoke(studio,"TestWordIndex",0,"25")!;Invoke(studio,"TestTool","Correct");Invoke(studio,"TestSelect",0,c,c);Invoke(studio,"TestFinish",0);Invoke(studio,"TestInline","30");
                Check(((string)Invoke(studio,"TestEditKinds")!).EndsWith("correct"),"text correction opens inline editor and records the replacement");
                Invoke(studio,"RotatePages",new[]{0},90);Check((int)Invoke(studio,"TestRotation",0)! ==90,"page rotates in 90° steps");
                Invoke(studio,"Undo");Check((int)Invoke(studio,"TestRotation",0)! ==0,"rotation is undoable");Invoke(studio,"Redo");
                await (Task)Invoke(studio,"SearchAsync","Malzeme")!;Check((int)studio.GetType().GetProperty("TestHits",Private)!.GetValue(studio)!>=3,"search finds hits on all pages");
                await (Task)Invoke(studio,"OcrPageAsync",2)!;Check(((string)studio.GetType().GetProperty("TestStatus",Private)!.GetValue(studio)!).StartsWith("OCR:"),"OCR adds selectable lines on a scanned page: "+(string)studio.GetType().GetProperty("TestStatus",Private)!.GetValue(studio)!);
                Capture(studio,"pdf-studio.png");
                var saved=Path.Combine(Path.GetTempPath(),"ms-pdf-"+Guid.NewGuid().ToString("N")+".pdf");
                Check(await (Task<bool>)Invoke(studio,"SaveToAsync",saved)!&&new FileInfo(saved).Length>1000,"edits and rotation are written to a new PDF copy");
                Check(File.ReadAllBytes(studioSample).Length>0&&!Field<bool>(studio,"_dirty"),"source PDF untouched and window clean after save");
                studio.Close();try{File.Delete(saved);}catch{}
            }
        }
        {
            var workerExeForCad=Environment.GetEnvironmentVariable("MAILSTUDIO_TECHNICAL_EXE");
            // General viewer: CNC / CSV / ZIP / security
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            static string WriteText(string f,string t){File.WriteAllText(f,t);return f;}
            var vdir=Path.Combine(Path.GetTempPath(),"ms-view-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(vdir);
            var nc=Path.Combine(vdir,"op10.nc");File.WriteAllText(nc,"%\nO1000 (FLANS)\nN10 G90 G54 X0. Y0.\nN20 T12 M6\nN30 S8000 M3\nN40 G1 Z-2. F600.\nM30\n%\n",new System.Text.UTF8Encoding(false));
            Check(DocumentViewerWindow.Classify(nc)==DocumentViewerWindow.Kind.Code,"NC programs open in the code viewer");
            var ncWin=new DocumentViewerWindow(nc,DocumentViewerWindow.Kind.Code);ncWin.Show();
            var ncEditor=Field<ICSharpCode.AvalonEdit.TextEditor>(ncWin,"_editor");Check(ncEditor.IsReadOnly&&ncEditor.Document.LineCount>=8&&ncEditor.SyntaxHighlighting?.Name=="G-Code"&&Field<TextBlock>(ncWin,"_status").Text.Contains("T12"),"CNC viewer is read-only, highlighted and lists tools");ncWin.Close();
            var csv=Path.Combine(vdir,"liste.csv");File.WriteAllText(csv,"Kod;Ad;Adet\nA1;\"Flanş; Ø120\";25\nB2;Mil;4\n",System.Text.Encoding.GetEncoding(1254));
            var csvWin=new DocumentViewerWindow(csv,DocumentViewerWindow.Kind.Csv);csvWin.Show();var grid=(DataGrid)Field<ContentControl>(csvWin,"_body").Content;var table=((System.Data.DataView)grid.ItemsSource).Table!;
            Check(table.Rows.Count==2&&table.Columns.Count==3&&(string)table.Rows[0][1]=="Flanş; Ø120","CSV table detects delimiter, quotes and Windows-1254 Turkish text");csvWin.Close();
            var zipPath=Path.Combine(vdir,"paket.zip");using(var z=System.IO.Compression.ZipFile.Open(zipPath,System.IO.Compression.ZipArchiveMode.Create)){System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z,nc,"cam/op10.nc");z.CreateEntry("bin/kur.exe");}
            var zipWin=new DocumentViewerWindow(zipPath,DocumentViewerWindow.Kind.Zip);zipWin.Show();Check(((ListView)Field<ContentControl>(zipWin,"_body").Content).Items.Count==2,"ZIP viewer lists archive entries");zipWin.Close();
            Check(FileViewers.Executable.Contains(".exe")&&FileViewers.Executable.Contains(".js")&&DocumentViewerWindow.Classify(WriteText(Path.Combine(vdir,"x.cmd"),"echo hi"))==DocumentViewerWindow.Kind.Code,"executables are never shell-opened; scripts open as text");
            // Excel editor (ClosedXML)
            var xlsx=Path.Combine(vdir,"hesap.xlsx");
            using(var wb=new ClosedXML.Excel.XLWorkbook()){var ws=wb.Worksheets.Add("Liste");ws.Cell(1,1).Value="Adet";ws.Cell(2,1).Value=10;ws.Cell(3,1).Value=20;ws.Cell(4,1).FormulaA1="SUM(A2:A3)";ws.Cell(1,2).Value="Flanş Ø120";wb.SaveAs(xlsx);}
            var sheetWin=new MailStudio.Views.Editors.SpreadsheetEditorWindow(xlsx);sheetWin.Show();
            string CellText(int r,int c)=>((Dictionary<long,MailStudio.Views.Editors.CellView>)sheetWin.GetType().GetField("_cache",Private)!.GetValue(sheetWin)!).TryGetValue(((long)r<<15)|(uint)c,out var v)?v.Text:"";
            Check(CellText(4,1)=="30"&&CellText(1,2)=="Flanş Ø120","Excel editor loads values, Unicode text and evaluates formulas");
            Invoke(sheetWin,"CommitInput",2,1,"15");Check(CellText(4,1)=="35","editing a cell recalculates dependent formulas");
            Invoke(sheetWin,"CommitInput",5,1,"=A4*2");Check(CellText(5,1)=="70","new formulas can be typed with =");
            Invoke(sheetWin,"Undo");Invoke(sheetWin,"Undo");Check(CellText(4,1)=="30"&&CellText(5,1)=="","undo restores previous values");Invoke(sheetWin,"Redo");Check(CellText(4,1)=="35","redo reapplies the edit");
            var view=Field<MailStudio.Views.Editors.SheetView>(sheetWin,"_view");view.Select(1,1,false);view.Select(1,2,true);
            Invoke(sheetWin,"Format","Kalın",(Action<ClosedXML.Excel.IXLStyle>)(st=>st.Font.Bold=true));
            var saved=Path.Combine(vdir,"hesap-kayit.xlsx");sheetWin.GetType().GetField("_savePath",Private)!.SetValue(sheetWin,saved);
            Check(await (Task<bool>)Invoke(sheetWin,"SaveAsync",false)!,"Excel editor saves to a new workbook");
            using(var back=new ClosedXML.Excel.XLWorkbook(saved)){var bw=back.Worksheet("Liste");Check(bw.Cell(2,1).GetDouble()==15&&bw.Cell(4,1).FormulaA1=="SUM(A2:A3)"&&bw.Cell(1,1).Style.Font.Bold&&bw.Cell(1,2).Style.Font.Bold,"saved workbook keeps edits, formulas and formatting");}
            using(var orig=new ClosedXML.Excel.XLWorkbook(xlsx)){Check(orig.Worksheet("Liste").Cell(2,1).GetDouble()==10,"source workbook is not modified");}
            Capture(sheetWin,"excel-editor.png");sheetWin.Close();
            // Word editor (Open XML + HtmlToOpenXml)
            var docx=Path.Combine(vdir,"rapor.docx");
            using(var wd=DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(docx,DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mp=wd.AddMainDocumentPart();var W=new Func<string,DocumentFormat.OpenXml.Wordprocessing.Paragraph>(t=>new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(t))));
                var head=W("Ölçüm Raporu");head.PrependChild(new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties(new DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId{Val="Heading1"}));
                var bold=new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold()),new DocumentFormat.OpenXml.Wordprocessing.Text("Kritik ölçü:")),new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(" Ø120 h7"){Space=DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve}));
                var wtable=new DocumentFormat.OpenXml.Wordprocessing.Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(new DocumentFormat.OpenXml.Wordprocessing.TableCell(W("Kod")),new DocumentFormat.OpenXml.Wordprocessing.TableCell(W("Adet"))),new DocumentFormat.OpenXml.Wordprocessing.TableRow(new DocumentFormat.OpenXml.Wordprocessing.TableCell(W("A1")),new DocumentFormat.OpenXml.Wordprocessing.TableCell(W("25"))));
                var sect=new DocumentFormat.OpenXml.Wordprocessing.SectionProperties(new DocumentFormat.OpenXml.Wordprocessing.PageSize{Width=16838,Height=11906,Orient=DocumentFormat.OpenXml.Wordprocessing.PageOrientationValues.Landscape});
                mp.Document=new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(head,bold,wtable,sect));mp.Document.Save();
            }
            var read=MailStudio.Views.Editors.DocxHtml.Read(docx);
            Check(read.Html.Contains("<h1>Ölçüm Raporu</h1>")&&read.Html.Contains("<b>Kritik ölçü:</b>")&&read.Html.Contains("<table")&&read.Html.Contains(">25<")&&Math.Abs(read.PageWidthPx-16838/15.0)<1,"Word reader keeps headings, bold runs, tables and page width");
            var wordWin=new MailStudio.Views.Editors.WordEditorWindow(docx);wordWin.Show();await Until(()=>Field<bool>(wordWin,"_ready"),30);
            await Field<WebView2>(wordWin,"_web").ExecuteScriptAsync("document.querySelector('#doc table td').innerText='Z9'; doc.insertAdjacentHTML('beforeend','<p>Yeni satır <i>eklendi</i></p>')");
            var wsaved=Path.Combine(vdir,"rapor-kayit.docx");Check(await wordWin.SaveAsync(false,wsaved),"Word editor saves a new document");
            using(var wd=DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(wsaved,false))
            {
                var body=wd.MainDocumentPart!.Document!.Body!;var text=body.InnerText;
                Check(text.Contains("Ölçüm Raporu")&&text.Contains("Z9")&&text.Contains("Yeni satır eklendi")&&body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().Any(),"saved document contains edited table and new paragraph");
                Check(body.Elements<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().LastOrDefault()?.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.PageSize>()?.Orient?.Value==DocumentFormat.OpenXml.Wordprocessing.PageOrientationValues.Landscape,"page setup (landscape) is preserved from the source");
            }
            Capture(wordWin,"word-editor.png");wordWin.Close();
            // PowerPoint editor (Open XML)
            if(Environment.GetEnvironmentVariable("MAILSTUDIO_PPTX_SAMPLE") is {} pptxSample&&File.Exists(pptxSample))
            {
                var pptx=Path.Combine(vdir,"sunum.pptx");File.Copy(pptxSample,pptx);
                var slideWin=new MailStudio.Views.Editors.SlideEditorWindow(pptx);slideWin.Show();
                var slides=(System.Collections.IList)slideWin.GetType().GetField("_slides",Private)!.GetValue(slideWin)!;Check(slides.Count==3,"PowerPoint editor lists slides");
                object Items(int i)=>slides[i]!.GetType().GetField("Items")!.GetValue(slides[i])!;
                var second=(System.Collections.IList)Items(1);var body2=second.Cast<object>().First(o=>(string)o.GetType().GetField("Text")!.GetValue(o)!=null&&((string)o.GetType().GetField("Text")!.GetValue(o)!).Contains("Malzeme"));
                body2.GetType().GetField("Text")!.SetValue(body2,"Malzeme: Ti-6Al-4V\nAdet: 40\nTeslim: 2 hafta\nKalite: AS9100");
                slides[2]!.GetType().GetField("Deleted")!.SetValue(slides[2],true);
                var psaved=Path.Combine(vdir,"sunum-kayit.pptx");Invoke(slideWin,"Write",pptx,psaved);
                using(var pd=DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(psaved,false))
                {
                    var ids=pd.PresentationPart!.Presentation!.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToList();
                    var s2=(DocumentFormat.OpenXml.Packaging.SlidePart)pd.PresentationPart.GetPartById(ids[1].RelationshipId!.Value!);var t2=s2.Slide!.InnerText;
                    Check(ids.Count==2&&t2.Contains("Ti-6Al-4V")&&t2.Contains("AS9100")&&!t2.Contains("7075"),"PowerPoint edits text (adding paragraphs) and deletes slides");
                    Check(s2.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>().Count(p=>p.InnerText.Length>0)>=5,"new lines become separate paragraphs with the original formatting");
                }
                slideWin.Close();
            }
            // Working without Office: PDF view and editable copies of old formats with the bundled LibreOffice
            if(Environment.GetEnvironmentVariable("MAILSTUDIO_FORCE_LIBREOFFICE")=="1"&&Environment.GetEnvironmentVariable("MAILSTUDIO_LEGACY_SAMPLES") is {} legacyDir&&Directory.Exists(legacyDir))
            {
                Check(MailStudio.Services.OfficeConverter.LibreOfficePath!=null&&MailStudio.Services.OfficeConverter.EngineName(MailStudio.Services.OfficeConverter.App.Word)=="LibreOffice","bundled LibreOffice is found and used without Microsoft Office");
                foreach(var f in Directory.GetFiles(legacyDir))
                {
                    var app=MailStudio.Services.OfficeConverter.AppFor(Path.GetExtension(f));if(app==null)continue;
                    var copy=Path.Combine(vdir,"eski"+Path.GetExtension(f));File.Copy(f,copy,true);
                    var pdf=await MailStudio.Services.OfficeConverter.ToPdfAsync(copy,CancellationToken.None);
                    Check(File.Exists(pdf)&&File.ReadAllBytes(pdf).Take(5).SequenceEqual("%PDF-"u8.ToArray()),$"{Path.GetExtension(f)} converts to PDF without Office");
                    var ox=await MailStudio.Services.OfficeConverter.ToOpenXmlAsync(copy,CancellationToken.None);
                    Check(File.Exists(ox)&&MailStudio.Services.OfficeConverter.IsOpenXml(ox)&&new FileInfo(ox).Length>1000,$"{Path.GetExtension(f)} converts to an editable OpenXML copy without Office");
                    if(app==MailStudio.Services.OfficeConverter.App.Word){using var wd=DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ox,false);Check(wd.MainDocumentPart!.Document!.Body!.InnerText.Length>20,"converted Word copy keeps its text");}
                    var legacyEditor=await FileViewers.OpenEditorAsync(copy,null);Check(legacyEditor!=null,$"{Path.GetExtension(f)} opens in the editor without Office");legacyEditor?.Close();
                }
            }
            // 2D CAD (DXF/DWG) viewer
            if(Environment.GetEnvironmentVariable("MAILSTUDIO_CAD_SAMPLE") is {} cadSample&&File.Exists(cadSample))
            {
                var cadWin=new CadDrawingWindow(cadSample,workerExeForCad);cadWin.Show();
                var canvas=Field<CadCanvas>(cadWin,"_canvas");
                await Until(()=>canvas.GetType().GetField("_model",Private)!.GetValue(canvas) is CadModel,40);
                var model=(CadModel)canvas.GetType().GetField("_model",Private)!.GetValue(canvas)!;
                Check(model.Groups.Count>=3&&model.Bounds.Width>=170&&Field<ListBox>(cadWin,"_layers").Items.Count>=5,"DXF viewer loads vector groups, extents and layers");
                Field<TextBox>(cadWin,"_search").Text="FLANŞ";Invoke(cadWin,"Search");Check(Field<List<int>>(cadWin,"_hits").Count==1,"DXF text search finds Unicode text");
                Check(Field<ComboBox>(cadWin,"_layout").SelectedItem as string=="Model","Model space opens first");
                Capture(cadWin,"dxf-viewer.png");cadWin.Close();
            }
            if(Environment.GetEnvironmentVariable("MAILSTUDIO_DWG_SAMPLE") is {} dwgSample&&File.Exists(dwgSample))
            {
                var dwgWin=new CadDrawingWindow(dwgSample,workerExeForCad);dwgWin.Show();
                var dcanvas=Field<CadCanvas>(dwgWin,"_canvas");
                await Until(()=>dcanvas.GetType().GetField("_model",Private)!.GetValue(dcanvas) is CadModel,60);
                var dmodel=dcanvas.GetType().GetField("_model",Private)!.GetValue(dcanvas) as CadModel;
                Check(dmodel!=null&&dmodel.Groups.Count>=1&&dmodel.Bounds.Width>0,"DWG opens with the bundled converter");
                await Task.Delay(500);Capture(dwgWin,"dwg-viewer.png");dwgWin.Close();
            }
            // Conditional formatting window + AI
            var rulesWin=new FormatRulesWindow(new List<FormatRule>(),new List<MailHeader>{new(){FromName="Ahmet",FromAddress="a@tedarikci.test",Subject="Teklif",Date=DateTimeOffset.Now}},
                (prompt,_)=>Task.FromResult(prompt.Contains("<tarif>")?"{\"Name\":\"Tedarikçi\",\"Conditions\":[{\"Field\":\"from\",\"Value\":\"tedarikci\"}],\"TextColor\":\"#C42B1C\",\"Bold\":true}":"{}"),()=>"Test");
            rulesWin.Show();Field<TextBox>(rulesWin,"_aiBox").Text="tedarikçiden gelenler kırmızı kalın";await (Task)Invoke(rulesWin,"AiCreateAsync")!;
            Check(rulesWin.Rules.Count==1&&rulesWin.Rules[0].Bold&&rulesWin.Rules[0].TextColor=="#C42B1C"&&Field<TextBlock>(rulesWin,"_matchInfo").Text.Contains(L.IsEnglish?"1 of":"1 tanesi"),"AI creates a reviewable conditional formatting rule with live match count");
            Capture(rulesWin,"format-rules.png");rulesWin.Close();
            // Email rules window + AI
            var mailRulesStore = new RuleStore(Path.Combine(AppPaths.Root, "ui-rules.json"));
            var mailRulesWin = new RulesWindow(mailRulesStore, new List<MailHeader>{new(){FromName="Anna Keller",FromAddress="anna@example.test",Subject="Teklif talebi",Date=DateTimeOffset.Now}},
                (prompt,_)=>Task.FromResult(prompt.Contains("<tarif>")?"{\"Name\":\"Anna teklifleri\",\"Conditions\":[{\"Field\":\"from\",\"Value\":\"anna\"}],\"Actions\":[{\"Type\":\"move_to_folder\",\"Value\":\"Teklifler\"}],\"StopProcessing\":true,\"MatchAll\":true}":"{}"),()=>"Test");
            mailRulesWin.Show();
            Field<TextBox>(mailRulesWin,"_aiBox").Text="Anna'dan gelen teklifleri Teklifler klasörüne taşı";
            await (Task)Invoke(mailRulesWin,"AiCreateAsync")!;
            Check(mailRulesWin.Rules.Count==1&&mailRulesWin.Rules[0].Name=="Anna teklifleri"&&mailRulesWin.Rules[0].Actions[0].Type=="move_to_folder"&&Field<TextBlock>(mailRulesWin,"_matchInfo").Text.Contains(L.IsEnglish?"1 of":"1 tanesi"),"AI creates a reviewable email rule with live match count");
            Audit(mailRulesWin, "RulesWindow");
            Capture(mailRulesWin,"rules-window.png");
            mailRulesWin.Close();
        }
        var trayMain=new MainWindow();
        trayMain.Loaded-=(RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler),trayMain,trayMain.GetType().GetMethod("OnLoaded",Private)!);
        Field<MailCache>(trayMain,"_cache").Init();Field<AccountStore>(trayMain,"_store").Accounts.Add(account);
        trayMain.Show();Field<DispatcherTimer>(trayMain,"_syncTimer").Start();
        var closeChoice=MessageBoxResult.Cancel;
        var trayDraft=new ComposeWindow(new[]{account},account.Id,new(){Subject="Retained draft"},mail,(_,_)=>Task.FromResult(""),()=>"Test",()=>closeChoice){Owner=trayMain};
        trayDraft.Show();await Until(()=>trayDraft.IsEditorInitialized);
        trayMain.Close();
        Check(trayMain.IsRunningInTray && !Field<bool>(trayDraft,"_closed") && !trayDraft.IsVisible && Field<DispatcherTimer>(trayMain,"_syncTimer").IsEnabled,"X hides to tray while draft and background timer remain alive");
        var tray=Field<TrayController>(trayMain,"_tray");
        Invoke(trayMain,"ShowDesktopAlert",new FolderNode{AccountId=account.Id,FullName="INBOX",SortOrder=0},new List<MailHeader>{new(){AccountId=account.Id,Uid=33,UidValidity=77,Folder="INBOX",FromAddress="test@example.test",Subject="Synthetic tray notification"}});
        Check(!trayMain.IsVisible && Field<Action?>(tray,"_openMail")!=null,"new mail while hidden queues a tray notification without opening the main window");
        trayMain.ShowFromTray();await Until(()=>Field<bool>(trayMain,"_webReady"));
        Check(trayMain.IsVisible && trayDraft.IsVisible && ((TextBox)trayDraft.FindName("SubjectBox")).Text=="Retained draft","tray restore preserves the open draft and its content");
        Check(!await trayMain.RequestExitAsync() && tray.IsVisible && !Field<bool>(trayMain,"_closed"),"cancelled draft close cancels full tray exit");
        closeChoice=MessageBoxResult.No;
        var trayMenu=Field<System.Windows.Forms.ContextMenuStrip>(tray,"_menu");
        ((System.Windows.Forms.ToolStripMenuItem)trayMenu.Items[trayMenu.Items.Count-1]).PerformClick();
        await Until(()=>Field<bool>(trayMain,"_closed"));
        Check(!tray.IsVisible && !Field<DispatcherTimer>(trayMain,"_syncTimer").IsEnabled && Field<bool>(trayDraft,"_closed"),"tray Exit closes draft, disposes the icon and stops background work");
        new AppSettings{OllamaUrl="http://127.0.0.1:1"}.Save();
        var background=new MainWindow(startInTray:true);background.Show();
        await Until(()=>Field<bool>(background,"_initialized") && Field<DispatcherTimer>(background,"_syncTimer").IsEnabled);
        Check(background.IsRunningInTray && !background.OwnedWindows.OfType<AccountWindow>().Any(),"background launch initializes monitoring without opening account dialogs");
        background.ShowFromTray();var providerCount=((ComboBox)background.FindName("ProviderBox")).Items.Count;
        background.Close();background.ShowFromTray();
        Check(((ComboBox)background.FindName("ProviderBox")).Items.Count==providerCount,"restore initializes provider list and timers only once");
        Check(await background.RequestExitAsync(),"background instance exits completely on request");
        var restored=0;
        using(var primary=new SingleInstance(Path.Combine(AppPaths.Root,"instance-test")))
        using(var secondary=new SingleInstance(Path.Combine(AppPaths.Root,"instance-test")))
        {
            Check(primary.IsPrimary && !secondary.IsPrimary,"second launch cannot create a duplicate monitor");
            primary.Listen(Application.Current.Dispatcher,()=>restored++);secondary.RestorePrimary();await Until(()=>restored==1);
            Check(restored==1,"second launch signals the existing instance to restore");
        }
        if(args.Contains("--preview") || args.Contains("--drop-preview"))
        {
            var preview=new ComposeWindow(new[]{account},account.Id,new(){To="test@example.test",Cc="copy@example.test",Bcc="hidden@example.test",Subject="MailStudio — sentetik arayüz kontrolü",BodyHtml="<h3>MailStudio düzenleme kontrolü</h3><p>Bu pencere yalnızca sentetik test verisi içerir.</p><table><tr><td>Özellik</td><td>Durum</td></tr><tr><td>BCC / taslak koruması</td><td>Hazır</td></tr></table>"},mail,(_,_)=>Task.FromResult(""),()=>"Test");
            preview.Show(); await Until(()=>preview.IsEditorInitialized);
            if(args.Contains("--drop-preview"))
            {
                var sourceButton=new Button{Content="TEST DOSYALARINI BURADAN GÖVDEYE SÜRÜKLE",Height=38,Margin=new Thickness(96,0,12,0)};
                sourceButton.PreviewMouseLeftButtonDown+=(_,e)=>{e.Handled=true;DragDrop.DoDragDrop(sourceButton,new DataObject(DataFormats.FileDrop,dropFiles),DragDropEffects.Copy);};
                Grid.SetRow(sourceButton,3);((Grid)preview.Content).Children.Add(sourceButton);
                Console.WriteLine("DROP_PREVIEW_READY");
                await Until(()=>Field<System.Collections.ObjectModel.ObservableCollection<string>>(preview,"_attachments").Count==2,120);
                Check(Field<System.Collections.ObjectModel.ObservableCollection<string>>(preview,"_attachments").SequenceEqual(dropFiles),"real OLE drag from WPF into native WebView2 adds both files");
                sourceButton.Visibility=Visibility.Collapsed;
            }
            Console.WriteLine("PREVIEW_READY"); await Task.Delay(TimeSpan.FromSeconds(55)); Close(preview);
        }
        {
            // 1.6: skills, Excel preview and agent connection windows (agent configs redirected to a temp folder).
            var skillsWin = new MailStudio.Views.SkillsWindow((_, _) => Task.FromResult("")); skillsWin.Show(); await Task.Delay(400);
            var skills = Field<List<MailStudio.Services.Skills.Skill>>(skillsWin, "_skills");
            Check(skills.Count >= 5 && skills.All(k => k.Error == null), "skills window lists the built-in skills without errors");
            Audit(skillsWin, "SkillsWindow"); Capture(skillsWin, "skills-window.png"); skillsWin.Close();
            var xlsx = Path.Combine(AppContext.BaseDirectory, "Demo", "delivery-schedule-wk43.xlsx");
            if (!File.Exists(xlsx)) xlsx = Directory.GetFiles(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MailStudio", "Demo")), "delivery-schedule-wk43.xlsx").First();
            var plan = MailStudio.Services.Skills.SkillRunner.ParseExcel("{\"file\":\"delivery-schedule-wk43.xlsx\",\"summary\":\"Demo\",\"operations\":[{\"op\":\"set\",\"sheet\":\"Week 43\",\"cell\":\"G4\",\"value\":\"2026-10-30\"},{\"op\":\"fill\",\"sheet\":\"Week 43\",\"cell\":\"A4:H4\",\"color\":\"#FFF2CC\"}]}");
            var excelWin = new MailStudio.Views.ExcelPlanWindow(xlsx, plan); excelWin.Show(); await Task.Delay(400);
            Audit(excelWin, "ExcelPlanWindow"); Capture(excelWin, "excel-plan-window.png"); excelWin.Close();
            AgentConnector.TestRoot = Path.Combine(AppPaths.Root, "agent-test");
            try
            {
                var agentsWin = new MailStudio.Views.AgentsWindow(); agentsWin.Show(); await Task.Delay(400);
                Check(Field<StackPanel>(agentsWin, "_rows").Children.Count >= 9, "agents window shows a card per supported agent");
                Audit(agentsWin, "AgentsWindow"); Capture(agentsWin, "agents-window.png"); agentsWin.Close();
            }
            finally { AgentConnector.TestRoot = null; }
            Console.WriteLine("PASS skills, Excel preview and agents windows"); _passed++;
        }
        if (L.IsEnglish)
        {
            foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList()) Audit(w, w.GetType().Name);
            var accWin = new AccountWindow(null, new AppSettings(), new OAuthService(() => new AppSettings())); accWin.Show(); await Task.Delay(300); Audit(accWin, "AccountWindow"); Capture(accWin, "account-window.png"); accWin.Close();
            var contactsWin = new ContactsWindow(false); contactsWin.Show(); await Task.Delay(300); Audit(contactsWin, "ContactsWindow"); contactsWin.Close();
            var pickWin = new ContactsWindow(true); pickWin.Show(); await Task.Delay(300); Audit(pickWin, "ContactsWindow(pick)"); pickWin.Close();
            var cardWin = new ContactEditWindow(new Contact()); cardWin.Show(); await Task.Delay(300); Audit(cardWin, "ContactEditWindow"); cardWin.Close();
            var groupWin = new GroupEditWindow(new ContactGroup()); groupWin.Show(); await Task.Delay(300); Audit(groupWin, "GroupEditWindow"); groupWin.Close();
            var auditRulesWin = new RulesWindow(new RuleStore(Path.Combine(AppPaths.Root, "audit-rules.json")), new List<MailHeader>(), (_, _) => Task.FromResult(""), () => "Test"); auditRulesWin.Show(); await Task.Delay(300); Audit(auditRulesWin, "RulesWindow"); auditRulesWin.Close();
            foreach (var t in TurkishLooking) Console.WriteLine("REVIEW " + t);
            foreach (var t in Untranslated) Console.WriteLine("UNTRANSLATED " + t);
            Check(Untranslated.Count == 0, "English UI: every visible UI string is translated");
        }
        Console.WriteLine($"RESULT {_passed} WPF checks passed. Data: {AppPaths.Root}");
        Console.Out.Flush(); Environment.Exit(0);   // all checks passed; do not wait for leftover windows/threads
    }
}


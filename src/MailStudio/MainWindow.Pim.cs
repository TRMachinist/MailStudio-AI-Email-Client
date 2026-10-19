using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Calendar;
using MailStudio.Views;
using MailStudio.Views.Calendar;
using MailStudio.Views.Quotes;

namespace MailStudio;

public enum PimModule { Mail, Calendar, People, Tasks, Quotes }

/// <summary>Calendar, tasks and reminders inside the main window (Outlook's module bar).</summary>
public partial class MainWindow : ITaskHost, IQuoteHost
{
    readonly CalendarStore _calendar = new();
    readonly TaskStore _tasks = new();
    readonly QuoteStore _quoteStore = new();
    CalendarView? _calendarView;
    TasksView? _tasksView;
    QuoteDeskView? _quoteDeskView;
    PimModule _module = PimModule.Mail;
    ReminderWindow? _reminderWindow;
    readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    HashSet<string> _remindedKeys = new();
    string? _mailTitle;

    public PimModule Module => _module;
    public CalendarStore CalendarStore => _calendar;
    public TaskStore TaskStore => _tasks;
    public QuoteStore QuoteStore => _quoteStore;
    public CalendarView? CalendarViewForTests => _calendarView;
    public TasksView? TasksViewForTests => _tasksView;
    public QuoteDeskView? QuoteDeskViewForTests => _quoteDeskView;

    void InitPim()
    {
        _reminderTimer.Tick += (_, _) => PimTick();
        _reminderTimer.Start();
        Dispatcher.BeginInvoke(PimTick, DispatcherPriority.ApplicationIdle);
    }

    void OnNavMail(object sender, RoutedEventArgs e) => SwitchModule(PimModule.Mail);
    void OnNavCalendar(object sender, RoutedEventArgs e) => SwitchModule(PimModule.Calendar);
    void OnNavTasks(object sender, RoutedEventArgs e) => SwitchModule(PimModule.Tasks);
    void OnNavQuotes(object sender, RoutedEventArgs e) => SwitchModule(PimModule.Quotes);

    void OnRibbonTabClick(object sender, RoutedEventArgs e)
    {
        // Ribbon tabs belong to Mail; the Home tab keeps the calendar / task commands of the open module
        if (_module != PimModule.Mail && sender is RadioButton { Tag: string tag } && tag != "home") SwitchModule(PimModule.Mail);
        else if (_module != PimModule.Mail) ShowModuleRibbon();
    }

    public void SwitchModule(PimModule m)
    {
        if (m == PimModule.People) { OnAddressBook(this, new RoutedEventArgs()); return; }
        if (m == _module) { if (m == PimModule.Calendar) _calendarView?.GoTo(DateTime.Today); return; }
        if (_module == PimModule.Mail) _mailTitle = Title;
        _module = m;
        var mail = m == PimModule.Mail;
        PimHost.Visibility = PimSide.Visibility = mail ? Visibility.Collapsed : Visibility.Visible;
        // The mail WebView is a native window drawn above all WPF content ("airspace"): hide it, an overlay cannot cover it
        ReadingPane.Visibility = mail ? Visibility.Visible : Visibility.Hidden;
        FolderTree.Visibility = mail ? Visibility.Visible : Visibility.Collapsed;
        if (!mail) NoAccountHint.Visibility = Visibility.Collapsed;
        else if (_store.Accounts.Count == 0) NoAccountHint.Visibility = Visibility.Visible;
        foreach (var (button, module) in new[] { (NavMail, PimModule.Mail), (NavCalendar, PimModule.Calendar), (NavTasks, PimModule.Tasks), (NavQuotes, PimModule.Quotes) })
            if (module == m) button.SetResourceReference(ForegroundProperty, "AccentPrimaryBrush"); else button.ClearValue(ForegroundProperty);
        switch (m)
        {
            case PimModule.Calendar:
                _calendarView ??= new CalendarView(_calendar, this);
                PimHost.Content = _calendarView; PimSide.Content = _calendarView.SidePanel;
                Title = L.T("Takvim") + " - " + L.ProductName;
                _calendarView.Refresh();
                Dispatcher.BeginInvoke(() => _calendarView.Focus(), DispatcherPriority.Input);
                break;
            case PimModule.Tasks:
                _tasksView ??= new TasksView(_tasks, this);
                PimHost.Content = _tasksView; PimSide.Content = _tasksView.SidePanel;
                Title = L.T("Görevler") + " - " + L.ProductName;
                _tasksView.Refresh();
                break;
            case PimModule.Quotes:
                _quoteDeskView ??= new QuoteDeskView(_quoteStore, this);
                PimHost.Content = _quoteDeskView; PimSide.Content = _quoteDeskView.SidePanel;
                Title = L.T("Teklif Masası") + " - " + L.ProductName;
                _quoteDeskView.Refresh();
                break;
            default:
                PimHost.Content = null; PimSide.Content = null;
                if (_mailTitle != null) Title = _mailTitle;
                break;
        }
        ShowModuleRibbon();
    }

    void ShowModuleRibbon()
    {
        foreach (var r in new UIElement?[] { _calendarView?.Ribbon, _tasksView?.Ribbon, _quoteDeskView?.Ribbon })
            if (r != null) { RibbonGrid.Children.Remove(r); }
        var mail = _module == PimModule.Mail;
        foreach (var p in new UIElement[] { RibbonHome, RibbonSendRecv, RibbonFolder, RibbonView, RibbonAi }) p.Visibility = Visibility.Collapsed;
        if (mail)
        {
            if (TabHome.IsChecked != true) TabHome.IsChecked = true; else RibbonHome.Visibility = Visibility.Visible;
            return;
        }
        if (TabHome.IsChecked != true) TabHome.IsChecked = true;
        foreach (var p in new UIElement[] { RibbonHome, RibbonSendRecv, RibbonFolder, RibbonView, RibbonAi }) p.Visibility = Visibility.Collapsed;
        var ribbon = _module switch
        {
            PimModule.Calendar => _calendarView?.Ribbon,
            PimModule.Tasks => _tasksView?.Ribbon,
            PimModule.Quotes => _quoteDeskView?.Ribbon,
            _ => null
        };
        if (ribbon != null) { RibbonGrid.Children.Add(ribbon); ribbon.Visibility = Visibility.Visible; }
    }

    // ===================================================================== reminders

    void PimTick()
    {
        // Items an agent added through the MCP server appear without a restart
        _calendar.ReloadIfChanged(); _tasks.ReloadIfChanged();
        var expiredCount = _followUpStore.CheckExpired(_tasks);
        if (expiredCount > 0) _tasksView?.Refresh();
        // Quote deadlines check
        var quoteOverdueCount = 0;
        foreach (var q in _quoteStore.Quotes)
        {
            if ((q.IsDeadlineToday || q.IsDeadlineOverdue) && q.Status is QuoteStatus.New or QuoteStatus.InProgress)
            {
                var sourceKey = $"quote|{q.Id}";
                if (_tasks.FindBySource(sourceKey) == null)
                {
                    _tasks.Save(new TaskItem
                    {
                        Title = L.F("Teklif Son Tarihi: {0} ({1})", q.PartNumber, q.Customer),
                        DueDate = q.Deadline?.Date ?? DateTime.Today,
                        Priority = TaskPriority.High,
                        Notes = L.F("Parça: {0}, Rev: {1}, Adet: {2}\n{3}", q.PartNumber, q.Revision, q.QuantityDisplay, q.Notes),
                        SourceMail = sourceKey,
                        SourceSubject = q.SourceSubject ?? q.PartNumber,
                        SourceFrom = q.ContactEmail ?? q.Contact,
                        ReminderAt = DateTime.Today.AddHours(9)
                    });
                    quoteOverdueCount++;
                }
            }
        }
        if (quoteOverdueCount > 0) _tasksView?.Refresh();
        var now = DateTime.Now;
        var due = _calendar.DueReminders(now).Select(o => "e" + o.Key).Concat(_tasks.DueReminders(now).Select(t => "t" + t.Id)).ToHashSet();
        if (due.Count > 0)
        {
            _reminderWindow ??= CreateReminderWindow();
            _reminderWindow.Update(now);
            if (due.Except(_remindedKeys).Any())
            {
                if (!_reminderWindow.IsVisible) _reminderWindow.Show();
                _reminderWindow.Activate();
                System.Media.SystemSounds.Asterisk.Play();
                if (_tray != null && (!IsVisible || WindowState == WindowState.Minimized))
                    _tray.ShowReminder(L.T("Anımsatıcı"), string.Join("\n", _calendar.DueReminders(now).Select(o => o.Event.Title + " · " + PimUi.RangeText(o)).Concat(_tasks.DueReminders(now).Select(t => t.Title)).Take(3)), () => _reminderWindow?.Activate());
            }
        }
        else if (_reminderWindow?.IsVisible == true) _reminderWindow.Hide();
        _remindedKeys = due;
        if (_module == PimModule.Calendar && _calendarView != null && Mouse.Captured == null && now.Minute % 5 == 0 && now.Second < 30) _calendarView.Refresh();
    }

    ReminderWindow CreateReminderWindow()
    {
        var w = new ReminderWindow(_calendar, _tasks)
        {
            OpenEvent = o => { Activate(); SwitchModule(PimModule.Calendar); _calendarView?.GoTo(o.Start.Date); _calendarView?.Open(o); },
            OpenTask = t => { Activate(); SwitchModule(PimModule.Tasks); },
        };
        if (Icon != null) w.Icon = Icon;
        return w;
    }

    void ClosePim() { _reminderTimer.Stop(); _reminderWindow?.ForceClose(); }

    // ===================================================================== mail → task / meeting

    void OnMailToTask(object sender, RoutedEventArgs e)
    {
        var mails = MessageList.SelectedItems.OfType<MailHeader>().ToList();
        if (mails.Count == 0 && _current != null) mails.Add(_current);
        if (mails.Count == 0) { Status(L.T("Önce listeden bir ileti seçin.")); return; }
        var added = 0;
        foreach (var m in mails)
        {
            var key = $"{m.AccountId}|{m.Folder}|{m.Uid}";
            if (_tasks.FindBySource(key) is { Completed: false }) continue;
            _tasks.Save(TasksView.FromMail(m, DateTime.Today)); added++;
        }
        Status(added == 0 ? L.T("Bu mail zaten görev listesinde.") : added == 1 ? L.F("Görev eklendi: {0} (son tarih bugün)", mails[0].SubjectDisplay) : L.F("{0} mail görev listesine eklendi.", added));
    }

    void OnMailToMeeting(object sender, RoutedEventArgs e)
    {
        if (_current is not { } h) { Status(L.T("Önce listeden bir ileti seçin.")); return; }
        var people = ContactStore.SplitAddresses($"\"{h.FromName}\" <{h.FromAddress}>; {h.To}; {_currentContent?.Cc}")
            .Where(p => !IsOwn(p.Address)).Select(p => p.Name.Length > 0 ? $"{p.Name} <{p.Address}>" : p.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var start = DateTime.Today.AddDays(1).AddHours(10);
        var ev = new CalendarEvent
        {
            Title = System.Text.RegularExpressions.Regex.Replace(h.Subject, @"^\s*((re|fw|fwd|ynt|ilt|ileti|yanıt)\s*:\s*)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            Start = start, End = start.AddMinutes(30), Attendees = people, SourceMail = $"{h.AccountId}|{h.Folder}|{h.Uid}",
            Notes = (_currentContent?.Text ?? h.Preview).Trim() is { Length: > 0 } t ? (t.Length > 1500 ? t[..1500] + "…" : t) : ""
        };
        var w = new EventWindow(ev, _calendar, isNew: true, meeting: true) { Owner = this, PickAttendees = ((IPimHost)this).PickAttendees, IsOwnAddress = IsOwn };
        if (w.ShowDialog() != true) return;
        _calendar.Save(ev);
        Status(L.F("Takvime eklendi: {0} · {1}", ev.Title, ev.Start.ToString("g", L.Culture)));
        if (w.SendInvitations) ((IPimHost)this).ComposeMeetingMail(ev, "REQUEST");
    }

    void OnChatToTasks(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage msg }) return;
        var lines = TaskStore.ParseTaskLines(msg.Text);
        if (lines.Count == 0) { Status(L.T("Cevapta madde işaretli görev satırı bulunamadı.")); return; }
        var source = _current;
        var n = _tasks.AddRange(lines.Select(l => new TaskItem
        {
            Title = l, SourceMail = source == null ? null : $"{source.AccountId}|{source.Folder}|{source.Uid}",
            SourceSubject = source?.Subject ?? "", SourceFrom = source?.FromDisplay ?? ""
        }));
        Status(n == 0 ? L.T("Bu görevler zaten listede.") : L.F("{0} görev eklendi (Görevler, Ctrl+4).", n));
    }

    // ===================================================================== invitation bar in the reading pane

    void ShowInvitation(MailHeader h, MailContent c)
    {
        InviteBar.Visibility = Visibility.Collapsed;
        InviteBar.Child = null;
        UiTranslator.SetSkip(InviteBar, true);
        if (string.IsNullOrWhiteSpace(c.CalendarIcs)) return;
        IcsCalendar cal;
        try { cal = Ics.Parse(c.CalendarIcs); } catch { return; }
        var ev = cal.Events.FirstOrDefault();
        if (ev == null) return;
        var source = $"{h.AccountId}|{h.Folder}|{h.Uid}";
        var existing = _calendar.FindByUid(ev.Uid);
        var panel = new StackPanel();
        var info = new StackPanel();
        string head = cal.IsCancel ? L.T("Toplantı iptal edildi") : cal.IsReply ? L.T("Toplantı yanıtı") : cal.IsInvitation ? L.T("Toplantı daveti") : L.T("Takvim öğesi");
        info.Children.Add(PimUi.Text("\U0001F4C5 " + head + ": " + ev.Title, 13, bold: true));
        var occ = new Occurrence(ev, ev.Start, ev.End);
        var when = (ev.AllDay ? ev.Start.ToString("D", L.Culture) : ev.Start.ToString("dddd, d MMMM yyyy", L.Culture) + "  " + PimUi.RangeText(occ)) +
                   (ev.Repeat != null ? "  ·  " + Recurrence.Describe(ev.Repeat, ev.Start) : "") + (ev.Location.Length > 0 ? "  ·  " + ev.Location : "");
        info.Children.Add(PimUi.Text(when, 12, brush: "TextSecondaryBrush"));
        if (cal.IsReply)
        {
            foreach (var (who, stat) in cal.Replies)
                info.Children.Add(PimUi.Text(who + ": " + stat switch { "ACCEPTED" => L.T("kabul etti"), "TENTATIVE" => L.T("belki dedi"), "DECLINED" => L.T("reddetti"), _ => stat.ToLowerInvariant() }, 12));
        }
        else if (existing != null && !cal.IsCancel)
        {
            var status = existing.Response switch
            {
                MeetingResponse.Accepted => L.T("Kabul ettiniz; takviminizde."), MeetingResponse.Tentative => L.T("Belki dediniz; takviminizde."),
                MeetingResponse.Declined => L.T("Reddettiniz."), MeetingResponse.Organizer => L.T("Düzenleyen sizsiniz."), _ => L.T("Takviminizde, henüz yanıt vermediniz.")
            };
            if (ev.Sequence > existing.Sequence) status = L.T("Toplantı güncellendi; yeni saati kabul etmek için yeniden yanıtlayın.");
            info.Children.Add(PimUi.Text(status, 12, brush: "AccentPrimaryBrush"));
        }
        if (!cal.IsCancel && !cal.IsReply && !ev.AllDay)
        {
            var conflicts = _calendar.Conflicts(ev.Start, ev.End, existing?.Id);
            if (conflicts.Count > 0) info.Children.Add(PimUi.Text("⚠ " + L.F("Çakışıyor: {0}", string.Join(", ", conflicts.Take(2).Select(o => o.Event.Title + " " + PimUi.RangeText(o)))), 12, brush: "StatusDangerBrush"));
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button B(string text, Action a, bool primary = false)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 2, 6, 2), Padding = new Thickness(10, 3, 10, 3) };
            b.SetResourceReference(StyleProperty, primary ? "MsPrimaryButtonStyle" : "FlatButton");
            b.Click += (_, _) => a(); buttons.Children.Add(b); return b;
        }
        if (cal.IsCancel)
        {
            if (existing != null) B(L.T("Takvimden kaldır"), () => { _calendar.Delete(existing); Status(L.F("Takvimden kaldırıldı: {0}", existing.Title)); ShowInvitation(h, c); }, primary: true);
        }
        else if (cal.IsInvitation)
        {
            B(L.T("Kabul et"), () => RespondInvitation(h, c, cal, MeetingResponse.Accepted, source), primary: true);
            B(L.T("Belki"), () => RespondInvitation(h, c, cal, MeetingResponse.Tentative, source));
            B(L.T("Reddet"), () => RespondInvitation(h, c, cal, MeetingResponse.Declined, source));
        }
        else if (!cal.IsReply)
            B(existing == null ? L.T("Takvime ekle") : L.T("Takvimde güncelle"), () => { _calendar.Import(cal.Events); Status(L.F("Takvime eklendi: {0}", ev.Title)); ShowInvitation(h, c); }, primary: true);
        if (existing != null || !cal.IsReply) B(L.T("Takvimde göster"), () => { SwitchModule(PimModule.Calendar); _calendarView?.GoTo(ev.Start.Date); });
        foreach (var t in info.Children.OfType<TextBlock>()) { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }
        panel.Children.Add(info); panel.Children.Add(buttons);
        InviteBar.Child = panel;
        InviteBar.Visibility = Visibility.Visible;
    }

    void RespondInvitation(MailHeader h, MailContent c, IcsCalendar cal, MeetingResponse response, string source)
    {
        var stored = _calendar.ApplyInvitation(cal, response, source);
        if (stored == null) return;
        Status(response switch { MeetingResponse.Accepted => L.F("Kabul edildi, takvime eklendi: {0}", stored.Title), MeetingResponse.Tentative => L.F("Belki olarak takvime eklendi: {0}", stored.Title), _ => L.F("Reddedildi: {0}", stored.Title) });
        if (stored.Organizer.Length > 0 && MessageBox.Show(this, L.T("Düzenleyene yanıt gönderilsin mi? (Mail penceresi açılır, siz gönderirsiniz.)"), "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            ((IPimHost)this).ComposeMeetingMail(stored, "REPLY", response switch { MeetingResponse.Accepted => "ACCEPTED", MeetingResponse.Tentative => "TENTATIVE", _ => "DECLINED" });
        ShowInvitation(h, c);
    }

    // ===================================================================== IPimHost / ITaskHost

    Window IPimHost.HostWindow => this;

    string? IPimHost.PickAttendees(Window owner)
    {
        var w = new ContactsWindow(pickMode: true) { Owner = owner };
        if (w.ShowDialog() != true) return null;
        return string.Join("; ", new[] { w.PickedTo, w.PickedCc, w.PickedBcc }.Where(s => s.Length > 0));
    }

    bool IPimHost.IsOwnAddress(string address) => IsOwn(address);
    bool IsOwn(string address) => _store.Accounts.Any(a => a.Email.Equals(address.Trim(), StringComparison.OrdinalIgnoreCase));

    void IPimHost.Status(string text) => Status(text);

    void IPimHost.ComposeMeetingMail(CalendarEvent e, string method, string? partStat, IEnumerable<string>? to)
    {
        if (_store.Accounts.Count == 0) { MessageBox.Show(this, L.T("Önce bir hesap ekleyin."), "MailStudio"); return; }
        var account = (e.SourceMail?.Split('|')[0] is { } sid ? Acc(sid) : null)
                      ?? _store.Accounts.FirstOrDefault(a => e.Attendees.Any(x => Ics.AddressOf(x).Equals(a.Email, StringComparison.OrdinalIgnoreCase)))
                      ?? (_current != null ? Acc(_current.AccountId) : null) ?? _store.Accounts[0];
        var me = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : $"{account.DisplayName} <{account.Email}>";
        var when = new Occurrence(e, e.Start, e.End);
        var summary = new StringBuilder();
        summary.AppendLine(L.T("Ne zaman: ") + (e.AllDay ? e.Start.ToString("D", L.Culture) : e.Start.ToString("dddd, d MMMM yyyy", L.Culture) + " " + PimUi.RangeText(when)));
        if (e.Repeat != null) summary.AppendLine(L.T("Yinelenme: ") + Recurrence.Describe(e.Repeat, e.Start));
        if (e.Location.Length > 0) summary.AppendLine(L.T("Nerede: ") + e.Location);
        var req = new ComposeRequest { CalendarMethod = method };
        switch (method)
        {
            case "REPLY":
                req.To = e.Organizer;
                req.Subject = (partStat switch { "ACCEPTED" => L.T("Kabul edildi: "), "TENTATIVE" => L.T("Belki: "), _ => L.T("Reddedildi: ") }) + e.Title;
                req.Body = "";
                req.CalendarIcs = Ics.WriteCalendar(new[] { e }, "REPLY", replyFrom: me, partStat: partStat ?? "ACCEPTED");
                break;
            case "CANCEL":
                e.Sequence++;
                req.To = string.Join("; ", to ?? e.Attendees);
                req.Subject = L.T("İptal edildi: ") + e.Title;
                req.Body = L.T("Bu toplantı iptal edildi.") + "\n\n" + summary;
                req.CalendarIcs = Ics.WriteCalendar(new[] { e }, "CANCEL", organizer: me);
                break;
            default:
                if (e.Organizer.Length == 0) { e.Organizer = me; e.Response = MeetingResponse.Organizer; _calendar.Save(e); }
                req.To = string.Join("; ", to ?? e.Attendees);
                req.Subject = e.Title;
                req.Body = summary + (e.Notes.Length > 0 ? "\n" + e.Notes : "");
                req.CalendarIcs = Ics.WriteCalendar(new[] { e }, "REQUEST", organizer: e.Organizer);
                break;
        }
        OpenCompose(req, account.Id);
        Status(L.T("Toplantı maili hazır; kontrol edip Gönder'e basın."));
    }

    void IPimHost.OpenSourceMail(string source)
    {
        var parts = source.Split('|');
        if (parts.Length < 3 || !uint.TryParse(parts[2], out var uid)) return;
        var h = _cache.GetHeaders(parts[0], parts[1]).FirstOrDefault(x => x.Uid == uid);
        if (h == null) { Status(L.T("Kaynak mail bu bilgisayardaki kopyada bulunamadı (taşınmış veya silinmiş olabilir).")); return; }
        ((ITaskHost)this).OpenMail(h);
    }

    IReadOnlyList<MailHeader> ITaskHost.FlaggedMails() => _cache.GetFlagged();

    async void ITaskHost.SetMailFlag(MailHeader mail, bool flagged)
    {
        if (Acc(mail.AccountId) is not { } acc) return;
        try
        {
            await _mail.SetFlaggedAsync(acc, mail, flagged);
            mail.IsFlagged = flagged;
            foreach (var shown in _messages.Where(x => x.AccountId == mail.AccountId && x.Folder == mail.Folder && x.Uid == mail.Uid)) { shown.IsFlagged = flagged; Reformat(shown); }
            _tasksView?.Refresh();
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    async void ITaskHost.OpenMail(MailHeader h)
    {
        if (Acc(h.AccountId) is not { } acc) return;
        try
        {
            var content = _cache.GetBody(h.AccountId, h.Folder, h.Uid) ?? await _mail.GetContentAsync(acc, h, CancellationToken.None);
            new MessageWindow(h, content, _settings.BlockRemoteImages,
                reply: () => Reply(h, content, all: false),
                replyAll: () => Reply(h, content, all: true),
                forward: () => ForwardAsync(h, content),
                delete: () => DeleteMessagesAsync(new[] { h }),
                openAttachment: att => OpenAttachmentAsync(h, att)) { Owner = this }.Also(FileViewers.ReturnFocusOnClose).Show();
        }
        catch (Exception ex) { Status(L.T("İleti yüklenemedi: ") + ex.Message); }
    }

    void ITaskHost.ComposeReminder(TaskItem task)
    {
        if (_store.Accounts.Count == 0) { MessageBox.Show(this, L.T("Önce bir hesap ekleyin."), "MailStudio"); return; }
        var account = (_store.Accounts.FirstOrDefault(a => task.SourceMail != null && task.SourceMail.StartsWith(a.Id + "|")))
                      ?? (_current != null ? Acc(_current.AccountId) : null)
                      ?? _store.Accounts[0];

        var to = task.SourceFrom;
        var subj = task.SourceSubject.Length > 0 ? task.SourceSubject : task.Title;
        if (!subj.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) &&
            !subj.StartsWith("Ynt:", StringComparison.OrdinalIgnoreCase) &&
            !subj.StartsWith("Hatırlatma:", StringComparison.OrdinalIgnoreCase) &&
            !subj.StartsWith("Reminder:", StringComparison.OrdinalIgnoreCase))
        {
            subj = (L.IsEnglish ? "Reminder: " : "Hatırlatma: ") + subj;
        }

        var body = L.T("Merhaba,\n\nAşağıdaki konuda geri dönüşünüzü rica ederim.\n\nİyi çalışmalar.");
        if (task.SourceMail != null)
        {
            var parts = task.SourceMail.Split('|');
            if (parts.Length >= 3 && uint.TryParse(parts[2], out var uid))
            {
                var content = _cache.GetBody(parts[0], parts[1], uid);
                if (content != null && !string.IsNullOrWhiteSpace(content.Text))
                {
                    body += "\n\n--- " + L.T("Önceki İleti") + " ---\n" + content.Text;
                }
            }
        }

        var req = new ComposeRequest
        {
            To = to,
            Subject = subj,
            Body = body
        };
        OpenCompose(req, account.Id);
        Status(L.T("Hatırlatma maili hazır; kontrol edip Gönder'e basın."));
    }

    void IQuoteHost.ComposeQuoteReply(QuoteItem quote)
    {
        if (_store.Accounts.Count == 0) { MessageBox.Show(this, L.T("Önce bir hesap ekleyin."), "MailStudio"); return; }
        var account = (_store.Accounts.FirstOrDefault(a => quote.SourceMail != null && quote.SourceMail.StartsWith(a.Id + "|")))
                      ?? (_current != null ? Acc(_current.AccountId) : null)
                      ?? _store.Accounts[0];

        var to = !string.IsNullOrEmpty(quote.ContactEmail) ? quote.ContactEmail : quote.Contact;
        var subj = !string.IsNullOrEmpty(quote.SourceSubject)
            ? (!quote.SourceSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) && !quote.SourceSubject.StartsWith("Ynt:", StringComparison.OrdinalIgnoreCase) ? "Re: " + quote.SourceSubject : quote.SourceSubject)
            : L.F("Teklif: {0} ({1})", quote.PartNumber, quote.Customer);

        var body = L.T("Merhaba,\n\nTalep ettiğiniz parçaya ilişkin fiyat teklifimiz ektedir.\n\nİyi çalışmalar.");
        if (quote.SourceMail != null)
        {
            var parts = quote.SourceMail.Split('|');
            if (parts.Length >= 3 && uint.TryParse(parts[2], out var uid))
            {
                var content = _cache.GetBody(parts[0], parts[1], uid);
                if (content != null && !string.IsNullOrWhiteSpace(content.Text))
                {
                    body += "\n\n--- " + L.T("Önceki İleti") + " ---\n" + content.Text;
                }
            }
        }

        var req = new ComposeRequest
        {
            To = to,
            Subject = subj,
            Body = body,
            InReplyTo = quote.SourceMessageId
        };
        OpenCompose(req, account.Id);
        Status(L.T("Teklif yanıtı taslağı hazır; teklif dosyasını ekleyip gönderin."));
    }
}

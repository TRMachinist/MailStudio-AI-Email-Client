using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Ai;
using MailStudio.Services.Skills;
using MailStudio.Views;

namespace MailStudio;

/// <summary>Skills in the main window: buttons in the AI pane, runs with attachments, auto-run on new mail.</summary>
public partial class MainWindow
{
    readonly SkillStore _skillStore = new();
    TechnicalWorker? _skillWorker;
    readonly SemaphoreSlim _autoSkillGate = new(1, 1);
    string? _skillNotesShownFor;

    TechnicalWorker SkillWorker() => _skillWorker ??= new TechnicalWorker();

    void OnSkills(object sender, RoutedEventArgs e)
    {
        new SkillsWindow(AiCompleteAsync) { Owner = this }.ShowDialog();
        if (_current != null && _currentContent != null) OnSkillContext(_current, _currentContent);
    }

    void OnAgents(object sender, RoutedEventArgs e) => new AgentsWindow { Owner = this }.ShowDialog();

    /// <summary>Called when a mail is shown: skill buttons that fit this mail, and notes of automatic runs.</summary>
    void OnSkillContext(MailHeader h, MailContent c)
    {
        SkillActions.Children.Clear();
        List<Skill> skills;
        try { skills = _skillStore.Enabled().Where(s => s.RunsOnButton && s.AppliesTo(h, c.Attachments.Select(a => a.FileName))).ToList(); }
        catch { skills = new(); }
        foreach (var s in skills)
        {
            var b = new Button { Content = "⚡ " + s.Title, Style = (Style)FindResource("FlatButton"), FontSize = 11.5, Padding = new Thickness(7, 2, 7, 2), ToolTip = s.Description };
            var skill = s;
            b.Click += (_, _) => _ = RunSkillAsync(skill);
            SkillActions.Children.Add(b);
        }
        var key = $"{h.AccountId}|{h.Folder}|{h.Uid}";
        if (_skillNotesShownFor == key) return;
        _skillNotesShownFor = key;
        foreach (var n in SkillNotes.Get(h.AccountId, h.Folder, h.Uid))
            _chat.Add(new ChatMessage { Author = "⚡ " + n.Title + " · " + L.T("otomatik"), Text = n.Text, ShowActions = true });
        if (_chat.Count > 0) ScrollChat();
    }

    async Task<List<SkillAttachment>> ReadSkillAttachmentsAsync(Skill skill, MailHeader h, MailContent c, Action<string>? progress, CancellationToken ct)
    {
        var list = new List<SkillAttachment>();
        foreach (var att in c.Attachments.Where(a => skill.Accepts(a.FileName)).Take(6))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(L.F("{0} okunuyor...", att.FileName));
            try
            {
                var path = await DownloadSkillAttachmentAsync(h, att, ct);
                if (path == null) continue;
                var text = await AttachmentExtractor.ExtractAsync(path, SkillWorker, ocr: true, ct);
                list.Add(new SkillAttachment(att.FileName, text));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { list.Add(new SkillAttachment(att.FileName, "[" + L.T("okunamadı: ") + ex.Message + "]")); }
        }
        return list;
    }

    async Task<string?> DownloadSkillAttachmentAsync(MailHeader h, AttachmentInfo att, CancellationToken ct)
    {
        var acc = Acc(h.AccountId);
        if (acc == null) return null;
        var path = AttachmentPath(h, att);
        if (File.Exists(path)) return path;
        return await _mail.DownloadAttachmentAsync(acc, h, att, AttachmentDir(h), ct);
    }

    /// <summary>Runs a skill on the open mail and streams the result into the AI pane.</summary>
    async Task RunSkillAsync(Skill skill, string? userNote = null)
    {
        if (_aiCts != null) { Status(L.T("AI hâlâ cevap yazıyor; önce Durdur'a basın.")); return; }
        if (_current == null || _currentContent == null) { AddSystemChat(L.T("Önce listeden bir e-posta seçin.")); return; }
        if (ColAi.Width.Value == 0) SetAiPaneVisible(true);
        var h = _current; var c = _currentContent;
        var provider = CurrentProvider; var model = CurrentModel;
        _chat.Add(new ChatMessage { IsUser = true, Author = L.T("Sen"), Text = "⚡ " + skill.Title });
        var answer = new ChatMessage { Author = provider.Name + (string.IsNullOrEmpty(model) ? "" : " · " + model), Text = "…" };
        _chat.Add(answer); ScrollChat();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;
        AiSendButton.IsEnabled = false; AiStopButton.Visibility = Visibility.Visible;
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        try
        {
            var atts = await ReadSkillAttachmentsAsync(skill, h, c, m => { answer.Text = m; AiStatus.Text = m; }, ct);
            if (skill.WantsAttachments && !skill.Inputs.Contains("mail") && atts.Count == 0)
            { answer.Text = L.T("Bu mailde skill'in okuyabileceği bir ek yok."); return; }
            var mailBlock = await Task.Run(() => AiPrompts.MailBlock(h, c), ct);
            var prompt = SkillRunner.BuildPrompt(skill, mailBlock, atts, userNote);
            answer.Text = "…";
            AiStatus.Text = L.F("{0} düşünüyor...", provider.Name);
            await foreach (var chunk in AiStream.Background(token => provider.StreamAsync(prompt, model, token), ct))
            {
                sb.Append(chunk);
                if (skill.Output is SkillOutput.Note or SkillOutput.Reply) { answer.Text = sb.ToString().TrimStart(); ScrollChat(); }
                AiStatus.Text = L.F("{0} yazıyor... {1:0} sn", provider.Name, sw.Elapsed.TotalSeconds);
            }
            var result = sb.ToString().Trim();
            switch (skill.Output)
            {
                case SkillOutput.Note:
                case SkillOutput.Reply:
                    answer.Text = result; answer.ShowActions = true;
                    _history.Add(new ChatTurn("user", "[" + skill.Title + "]"));
                    _history.Add(new ChatTurn("assistant", result));
                    break;
                case SkillOutput.Organize:
                    var plan = SkillRunner.ParseOrganize(result);
                    answer.Text = await ApplyOrganizeAsync(h, plan);
                    break;
                case SkillOutput.Excel:
                    answer.Text = await ShowExcelPlanAsync(h, c, SkillRunner.ParseExcel(result), ct);
                    break;
            }
            AiStatus.Text = L.F("{0} · {1:0.0} sn", provider.Name, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) { answer.Text = sb.Length > 0 ? sb + "\n" + L.T("[durduruldu]") : L.T("[durduruldu]"); AiStatus.Text = L.T("Durduruldu."); }
        catch (Exception ex) { answer.IsError = true; answer.Text = Hint(provider, ex.Message); AiStatus.Text = L.T("Hata."); }
        finally
        {
            _aiCts?.Dispose(); _aiCts = null;
            AiSendButton.IsEnabled = true; AiStopButton.Visibility = Visibility.Collapsed;
            ScrollChat();
        }
    }

    async Task<string> ShowExcelPlanAsync(MailHeader h, MailContent c, ExcelEditPlan plan, CancellationToken ct)
    {
        var att = c.Attachments.FirstOrDefault(a => a.FileName.Equals(plan.File ?? "", StringComparison.OrdinalIgnoreCase))
                  ?? c.Attachments.FirstOrDefault(a => a.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || a.FileName.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase));
        if (att == null) return L.T("Mailde düzenlenecek bir Excel eki yok.");
        var path = await DownloadSkillAttachmentAsync(h, att, ct);
        if (path == null) return L.T("Excel eki indirilemedi.");
        var w = new ExcelPlanWindow(path, plan) { Owner = this };
        if (w.ShowDialog() != true || w.SavedPath == null) return plan.Summary + "\n" + L.T("(Değişiklikler kaydedilmedi.)");
        try { FileViewers.Open(w.SavedPath, this, new ComposeAiOptions(_providers, CurrentProvider.Name, CurrentModel)); } catch { }
        return plan.Summary + "\n" + L.F("Yeni dosya kaydedildi: {0}", w.SavedPath);
    }

    async Task<string> ApplyOrganizeAsync(MailHeader h, OrganizePlan plan)
    {
        var acc = Acc(h.AccountId);
        if (acc == null || plan.IsEmpty) return string.IsNullOrEmpty(plan.Reason) ? L.T("Değişiklik gerekmedi.") : plan.Reason;
        var done = new List<string>();
        if (plan.Flag is bool f && f != h.IsFlagged) { await _mail.SetFlaggedAsync(acc, h, f); h.IsFlagged = f; done.Add(f ? L.T("bayrak kondu") : L.T("bayrak kaldırıldı")); }
        if (plan.Read is bool r && r != h.IsRead) { await _mail.SetReadAsync(acc, h, r); h.IsRead = r; done.Add(r ? L.T("okundu yapıldı") : L.T("okunmadı yapıldı")); }
        if (!string.IsNullOrWhiteSpace(plan.MoveTo))
        {
            var target = _roots.FirstOrDefault(x => x.AccountId == acc.Id)?.Children
                .FirstOrDefault(n => n.FullName.Equals(plan.MoveTo, StringComparison.OrdinalIgnoreCase) || n.Name.Equals(plan.MoveTo, StringComparison.OrdinalIgnoreCase));
            if (target == null) done.Add(L.F("klasör bulunamadı: {0}", plan.MoveTo));
            else if (target.SortOrder == 3) done.Add(L.T("Silinmiş Öğeler'e taşıma yapılmadı"));
            else { await _mail.MoveAsync(acc, h, target.FullName); done.Add(L.F("{0} klasörüne taşındı", target.Name)); }
        }
        return (done.Count > 0 ? "✓ " + string.Join(", ", done) : L.T("Değişiklik gerekmedi.")) + (plan.Reason.Length > 0 ? "\n" + plan.Reason : "");
    }

    /// <summary>New mail arrived: run the skills the user switched to automatic. One mail at a time, never blocks the UI.</summary>
    async Task AutoRunSkillsAsync(List<MailHeader> fresh)
    {
        List<Skill> auto;
        try { auto = _skillStore.Enabled().Where(_skillStore.IsAuto).ToList(); } catch { return; }
        if (auto.Count == 0 || fresh.Count == 0) return;
        await _autoSkillGate.WaitAsync();
        try
        {
            foreach (var h in fresh.Take(10))
                foreach (var skill in auto.Where(s => s.AppliesTo(h, null)))
                {
                    if (_closed || _exitRequested) return;
                    var acc = Acc(h.AccountId); if (acc == null) continue;
                    try
                    {
                        Status(L.F("Skill çalışıyor: {0} · {1}", skill.Title, h.Subject));
                        var c = await _mail.GetContentAsync(acc, h, _lifetime.Token, markRead: false);
                        if (!skill.AppliesTo(h, c.Attachments.Select(a => a.FileName))) continue;
                        var atts = await ReadSkillAttachmentsAsync(skill, h, c, null, _lifetime.Token);
                        var prompt = SkillRunner.BuildPrompt(skill, AiPrompts.MailBlock(h, c), atts);
                        var provider = CurrentProvider; var model = CurrentModel;
                        var result = (await AiStream.CompleteAsync(t => provider.StreamAsync(prompt, model, t), _lifetime.Token)).Trim();
                        var text = skill.Output switch
                        {
                            SkillOutput.Organize => await ApplyOrganizeAsync(h, SkillRunner.ParseOrganize(result)),
                            SkillOutput.Excel => L.T("Bu skill Excel düzenlemesi önerdi; uygulamak için maili açıp skill'i yapay zeka panelinden çalıştırın."),
                            _ => result,
                        };
                        SkillNotes.Add(h.AccountId, h.Folder, h.Uid, new SkillNotes.Note(skill.Id, skill.Title, text, DateTimeOffset.Now));
                        Status(L.F("Skill tamamlandı: {0} · {1}", skill.Title, h.Subject));
                        if (_current?.Uid == h.Uid && _current.Folder == h.Folder && _currentContent != null) { _skillNotesShownFor = null; OnSkillContext(_current, _currentContent); }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Status(L.F("Skill çalışmadı ({0}): {1}", skill.Title, ex.Message)); }
                }
        }
        finally { _autoSkillGate.Release(); }
    }
}

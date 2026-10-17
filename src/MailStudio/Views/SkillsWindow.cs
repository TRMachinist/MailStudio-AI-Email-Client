using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MailStudio.Services;
using MailStudio.Services.Skills;
using Microsoft.Win32;

namespace MailStudio.Views;

/// <summary>
/// Skill manager: built-in and own skills, turn on/off, auto-run on new mail, edit SKILL.md, import / export,
/// and "describe it, AI writes the skill". AI output is only shown in the editor; the user saves it.
/// </summary>
public sealed class SkillsWindow : Window
{
    readonly SkillStore _store = new();
    readonly Func<string, CancellationToken, Task<string>> _ai;
    readonly ListBox _list = new();
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _meta = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock _desc = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly CheckBox _enabled = new() { Content = L.T("Açık (yapay zeka panelinde düğme olarak görünür)"), Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _auto = new() { Content = L.T("Yeni mail gelince otomatik çalıştır"), Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox _editor = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly Button _save, _copy, _delete, _export;
    readonly TextBox _aiBox = new() { MinHeight = 56, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    readonly Button _aiButton;
    readonly TextBlock _aiStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    List<Skill> _skills = new();
    Skill? _cur;
    bool _loading;
    string? _unsavedId;
    CancellationTokenSource? _aiCts;

    public SkillsWindow(Func<string, CancellationToken, Task<string>> ai)
    {
        _ai = ai;
        Title = L.T("Skill'ler"); Width = 1080; Height = 720; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        Closed += (_, _) => _aiCts?.Cancel();

        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        Content = root;
        var head = new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 0, 0, 1), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), BorderBrush = ThemeManager.Brush("BorderSubtleBrush") };
        var hs = new StackPanel(); head.Child = hs; root.Children.Add(head);
        hs.Children.Add(new TextBlock { Text = L.T("Skill'ler"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        hs.Children.Add(new TextBlock { Text = L.T("Skill, yapay zekaya tekrar eden bir işi tarif eden kuraldır: teklif analizi, resim kontrolü, STEP hammadde hesabı, Excel güncelleme… Mail ve ekleri (PDF, STEP, DXF, Excel) okunur; sonuç not, cevap taslağı, Excel kopyası veya bayrak/taşıma olur. Mail asla gönderilmez, ek dosyası asla değiştirilmez. Aynı SKILL.md dosyası Claude ve Codex skill'i olarak da çalışır."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = ThemeManager.Brush("TextSecondaryBrush") });

        var body = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(300) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1); root.Children.Add(body);

        // left: list + AI creator
        var left = new DockPanel(); body.Children.Add(left);
        var ai = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(ai, Dock.Bottom);
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; DockPanel.SetDock(bar, Dock.Bottom);
        left.Children.Add(ai); left.Children.Add(bar);
        var lh = new TextBlock { Text = L.T("Skill listesi"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }; DockPanel.SetDock(lh, Dock.Top);
        left.Children.Add(lh); left.Children.Add(_list);
        bar.Children.Add(Btn(L.T("Yeni"), (_, _) => NewSkill()));
        bar.Children.Add(Btn(L.T("İçe aktar..."), (_, _) => Import()));
        bar.Children.Add(Btn(L.T("Klasörü aç"), (_, _) => Process.Start(new ProcessStartInfo(SkillStore.UserFolder) { UseShellExecute = true })));
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: Skill s }) Show(s); };

        ai.Children.Add(new TextBlock { Text = L.T("Doğal dille anlat, yapay zeka skill'i yazsın"), FontWeight = FontWeights.SemiBold });
        ai.Children.Add(new TextBlock { Text = L.T("Örn: \"Tedarikçi gecikme bildirince ekteki Excel planında ilgili satırı sarı yap, yeni tarihi yaz\""), TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("TextSecondaryBrush"), Margin = new Thickness(0, 2, 0, 6) });
        ai.Children.Add(_aiBox);
        _aiButton = Btn(L.T("Skill oluştur"), async (_, _) => await AiCreateAsync());
        _aiButton.HorizontalAlignment = HorizontalAlignment.Left; _aiButton.Margin = new Thickness(0, 6, 0, 0);
        ai.Children.Add(_aiButton); ai.Children.Add(_aiStatus);

        // right: details + editor
        var right = new DockPanel(); Grid.SetColumn(right, 2); body.Children.Add(right);
        var info = new StackPanel(); DockPanel.SetDock(info, Dock.Top); right.Children.Add(info);
        info.Children.Add(_title); _meta.Foreground = ThemeManager.Brush("TextSecondaryBrush"); info.Children.Add(_meta); info.Children.Add(_desc);
        info.Children.Add(_enabled); info.Children.Add(_auto);
        _enabled.Checked += (_, _) => Toggle(); _enabled.Unchecked += (_, _) => Toggle();
        _auto.Checked += (_, _) => ToggleAuto(); _auto.Unchecked += (_, _) => ToggleAuto();
        info.Children.Add(new TextBlock { Text = "SKILL.md", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var st = _status; DockPanel.SetDock(st, Dock.Bottom);
        right.Children.Add(st); right.Children.Add(actions);
        _save = Btn(L.T("Kaydet"), (_, _) => Save());
        _copy = Btn(L.T("Kopyasını düzenle"), (_, _) => { if (_cur != null) { var c = _store.CopyToUser(_cur); Reload(c.Id); } });
        _export = Btn(L.T("Dışa aktar (.zip)..."), (_, _) => Export());
        _delete = Btn(L.T("Sil"), (_, _) => Delete());
        actions.Children.Add(_save); actions.Children.Add(_copy); actions.Children.Add(_export); actions.Children.Add(_delete);
        actions.Children.Add(Btn(L.T("Skill biçimi (yardım)"), (_, _) => OpenHelp()));
        right.Children.Add(_editor);
        _editor.TextChanged += (_, _) => { if (!_loading) _status.Text = L.T("Kaydedilmemiş değişiklik var."); };

        Reload(null);
    }

    Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3) };
        b.Click += click; return b;
    }

    void Reload(string? select)
    {
        _skills = _store.All();
        _list.Items.Clear();
        foreach (var s in _skills)
        {
            var sp = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            sp.Children.Add(new TextBlock { Text = (s.Enabled ? "" : "○ ") + s.Title, FontWeight = FontWeights.SemiBold, Opacity = s.Enabled ? 1 : .6 });
            var sub = s.Error != null ? L.T("Hatalı: ") + s.Error : (s.BuiltIn ? L.T("Hazır skill") : L.T("Benim skill'im")) + " · " + s.OutputText + (_store.IsAuto(s) ? " · " + L.T("otomatik") : "");
            sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = s.Error != null ? Brushes.IndianRed : ThemeManager.Brush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
            _list.Items.Add(new ListBoxItem { Content = sp, Tag = s });
        }
        var target = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((Skill)i.Tag).Id == select) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        if (target != null) _list.SelectedItem = target; else Show(null);
    }

    void Show(Skill? s)
    {
        _loading = true;
        _cur = s; _unsavedId = null;
        _title.Text = s?.Title ?? "";
        _meta.Text = s == null ? "" : $"{(s.BuiltIn ? L.T("Hazır skill") : L.T("Benim skill'im"))} · {L.T("Okur")}: {s.InputsText} · {L.T("Çalışır")}: {s.RunText} · {L.T("Sonuç")}: {s.OutputText}"
                                       + (s.When.Count > 0 ? " · " + L.T("Koşul") + ": " + string.Join(", ", s.When.Select(w => w.ToString())) : "");
        _desc.Text = s?.Error != null ? L.T("Hatalı: ") + s.Error : s?.Description ?? "";
        _enabled.IsChecked = s?.Enabled == true; _enabled.IsEnabled = s != null && s.Error == null;
        _auto.Visibility = s?.RunsOnNewMail == true ? Visibility.Visible : Visibility.Collapsed;
        _auto.IsChecked = s != null && _store.IsAuto(s);
        try { _editor.Text = s == null ? "" : File.ReadAllText(s.FilePath); } catch (Exception ex) { _editor.Text = ex.Message; }
        _editor.IsReadOnly = s?.BuiltIn == true;
        _save.IsEnabled = s is { BuiltIn: false };
        _copy.Visibility = s?.BuiltIn == true ? Visibility.Visible : Visibility.Collapsed;
        _delete.IsEnabled = s is { BuiltIn: false };
        _export.IsEnabled = s != null;
        _status.Text = s?.BuiltIn == true ? L.T("Hazır skill'ler değiştirilemez; düzenlemek için 'Kopyasını düzenle'.") : "";
        _loading = false;
    }

    void Toggle() { if (_loading || _cur == null) return; _store.SetEnabled(_cur, _enabled.IsChecked == true); Reload(_cur.Id); }
    void ToggleAuto() { if (_loading || _cur == null) return; _store.SetAuto(_cur, _auto.IsChecked == true); Reload(_cur.Id); }

    void NewSkill()
    {
        var md = Skill.Template("my-skill", L.T("Yeni skill"), L.T("Bu skill'in ne yaptığını tek cümleyle yazın."),
            L.T("# Yeni skill\n\nYapay zekanın ne yapmasını istediğinizi madde madde yazın:\n\n1. ...\n2. ..."), "mail, pdf", "button", "note");
        ShowDraft(md);
    }

    /// <summary>Puts an unsaved SKILL.md in the editor (new skill or AI result).</summary>
    void ShowDraft(string md)
    {
        _list.SelectedItem = null;
        _loading = true;
        _cur = null; _unsavedId = "new";
        _title.Text = L.T("Yeni skill (kaydedilmedi)"); _meta.Text = ""; _desc.Text = L.T("Düzenleyip Kaydet'e basın. Kaydetmeden önce dosya kontrol edilir.");
        _enabled.IsEnabled = false; _auto.Visibility = Visibility.Collapsed;
        _editor.IsReadOnly = false; _editor.Text = md;
        _save.IsEnabled = true; _copy.Visibility = Visibility.Collapsed; _delete.IsEnabled = false; _export.IsEnabled = false;
        _status.Text = "";
        _loading = false;
    }

    void Save()
    {
        try
        {
            if (_cur is { BuiltIn: false })
            {
                Skill.Parse(_editor.Text, _cur.Id);   // validate first
                File.WriteAllText(_cur.FilePath, _editor.Text.Replace("\r\n", "\n"));
                Reload(_cur.Id);
                _status.Text = L.T("Kaydedildi.");
            }
            else if (_unsavedId != null)
            {
                var s = _store.Save(_editor.Text);
                Reload(s.Id);
                _status.Text = L.F("Kaydedildi: {0}", s.Folder);
            }
        }
        catch (Exception ex) { _status.Text = L.T("Kaydedilemedi: ") + ex.Message; _status.Foreground = Brushes.IndianRed; return; }
        _status.Foreground = ThemeManager.Brush("TextSecondaryBrush");
    }

    void Import()
    {
        var dlg = new OpenFileDialog { Filter = L.T("Skill (SKILL.md veya .zip)|SKILL.md;*.zip;*.md|Tüm dosyalar|*.*"), Title = L.T("Skill içe aktar") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var path = dlg.FileName;
            if (Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path)!;
            var added = _store.Import(path);
            Reload(added[0].Id);
            _status.Text = L.F("{0} skill içe aktarıldı.", added.Count);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Skill içe aktar"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void Export()
    {
        if (_cur == null) return;
        var dlg = new SaveFileDialog { FileName = _cur.Id + ".zip", Filter = "Zip|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        SkillStore.ExportZip(_cur, dlg.FileName);
        _status.Text = L.F("Dışa aktarıldı: {0}", dlg.FileName);
    }

    void Delete()
    {
        if (_cur is not { BuiltIn: false }) return;
        if (MessageBox.Show(this, L.F("'{0}' skill'i silinsin mi?", _cur.Title), L.T("Skill'ler"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.Delete(_cur); Reload(null);
    }

    void OpenHelp()
    {
        var doc = Path.Combine(AppContext.BaseDirectory, "docs", "SKILLS.md");
        try { Process.Start(new ProcessStartInfo(File.Exists(doc) ? doc : "https://github.com/TRMachinist/MailStudio-AI-Email-Client/blob/main/docs/SKILLS.md") { UseShellExecute = true }); } catch { }
    }

    public const string GeneratorPrompt = """
        You are an expert at writing MailStudio skills. A skill is a SKILL.md file: YAML front matter, then Markdown instructions
        that an AI follows when the user runs the skill on an email and its attachments.

        Front matter format (keep exactly this structure):
        ---
        name: <lowercase-words-with-dashes>
        description: "<one sentence: what it does and when to use it>"
        mailstudio:
          title: "<short English title>"
          title_tr: "<short Turkish title>"
          inputs: [<some of: mail, pdf, step, dxf, xlsx, docx, pptx, csv, text>]
          run: [<button and/or new-mail>]
          when:                      # optional, only for new-mail skills
            subject: "<words separated by ;>"
          output: <note | reply | excel | organize>
        ---

        Outputs: note = analysis text; reply = body of a reply mail; excel = cell edits of an attached Excel file
        (applied to a copy after preview); organize = flag / mark read / move the mail. The skill can never send mail.
        Possible 'when' fields: from, subject, to, text, unread, flagged, attachment.

        Write clear numbered instructions for the AI. Tell it never to invent numbers and to say when information is missing.
        Answer with the complete SKILL.md only, no code fences, no explanations.

        The user wants this skill:
        """;

    async Task AiCreateAsync()
    {
        var want = _aiBox.Text.Trim();
        if (want.Length < 8) { _aiStatus.Text = L.T("Skill'in ne yapacağını bir iki cümleyle yazın."); return; }
        _aiCts?.Cancel(); _aiCts = new CancellationTokenSource();
        _aiButton.IsEnabled = false; _aiStatus.Text = L.T("Yapay zeka skill'i yazıyor...");
        try
        {
            var text = (await _ai(GeneratorPrompt + want, _aiCts.Token)).Trim();
            if (text.StartsWith("```")) text = string.Join('\n', text.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var start = text.IndexOf("---", StringComparison.Ordinal);
            if (start > 0) text = text[start..];
            Skill.Parse(text, "check");
            ShowDraft(text);
            _aiStatus.Text = L.T("Skill hazır. Sağda kontrol edip Kaydet'e basın.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _aiStatus.Text = L.T("Skill oluşturulamadı: ") + ex.Message; }
        finally { _aiButton.IsEnabled = true; }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Semantic;
using MailStudio.Views.Calendar;

namespace MailStudio.Views;

/// <summary>
/// Search by meaning ("which mail said the delivery slips?") over the local mail copy. The engine is either the
/// built-in n-gram vectors (no download) or an embedding model in the local Ollama; mail text never leaves the PC.
/// </summary>
public sealed class SemanticSearchWindow : Window
{
    readonly MailCache _cache;
    readonly Action<MailHeader> _open;
    readonly TextBox _query = new() { Height = 34, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
    readonly ComboBox _engine = new() { Height = 30, MinWidth = 260 };
    readonly ListView _results = new() { BorderThickness = new Thickness(0) };
    readonly TextBlock _status = PimUi.Text("", 12, brush: "TextSecondaryBrush");
    readonly ProgressBar _progress = new() { Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    readonly Button _update = new() { Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0) };
    SemanticIndex? _index;
    CancellationTokenSource? _work;
    const string LocalEngine = "";

    public IReadOnlyList<SemanticHit> Hits { get; private set; } = Array.Empty<SemanticHit>();
    public SemanticIndex? Index => _index;

    sealed record Engine(string Id, string Name) { public override string ToString() => Name; }

    public SemanticSearchWindow(MailCache cache, Action<MailHeader> open, string? initialQuery = null)
    {
        _cache = cache; _open = open;
        UiTranslator.SetSkip(this, true);
        Title = L.T("Anlamla ara");
        Width = 980; Height = 720; MinWidth = 700; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var top = new StackPanel { Margin = new Thickness(16, 14, 16, 6) };
        var row = new DockPanel();
        var go = new Button { Content = L.T("Ara"), Height = 34, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        go.SetResourceReference(StyleProperty, "RibbonPrimaryCmd"); go.Click += async (_, _) => await SearchAsync();
        DockPanel.SetDock(go, Dock.Right); row.Children.Add(go);
        _query.Text = initialQuery ?? "";
        _query.ToolTip = L.T("Ne aradığınızı cümleyle yazın: \"teslimatın gecikeceğini söyleyen mail\", \"flanş delik çapı değişikliği\"");
        row.Children.Add(_query);
        top.Children.Add(row);

        var engineRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        engineRow.Children.Add(new TextBlock { Text = L.T("Motor:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        engineRow.Children.Add(_engine);
        _update.Content = L.T("Dizini güncelle");
        _update.SetResourceReference(StyleProperty, "RibbonCmd"); _update.Click += async (_, _) => await UpdateIndexAsync();
        engineRow.Children.Add(_update);
        top.Children.Add(engineRow);
        var privacy = PimUi.Text(L.T("Tüm işlem bu bilgisayarda yapılır; mail metinleri dışarı gönderilmez."), 11.5, brush: "TextMutedBrush");
        privacy.Margin = new Thickness(0, 6, 0, 0); top.Children.Add(privacy);
        top.Children.Add(_progress);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = L.T("Uygunluk"), DisplayMemberBinding = new Binding("ScoreText"), Width = 80 });
        view.Columns.Add(new GridViewColumn { Header = L.T("Tarih"), DisplayMemberBinding = new Binding("Date"), Width = 120 });
        view.Columns.Add(new GridViewColumn { Header = L.T("Kimden"), DisplayMemberBinding = new Binding("From"), Width = 160 });
        var subject = new GridViewColumn { Header = L.T("Konu ve içerik"), Width = 560 };
        var tpl = new DataTemplate();
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        var t1 = new FrameworkElementFactory(typeof(TextBlock)); t1.SetBinding(TextBlock.TextProperty, new Binding("Subject")); t1.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var t2 = new FrameworkElementFactory(typeof(TextBlock)); t2.SetBinding(TextBlock.TextProperty, new Binding("Snippet")); t2.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); t2.SetValue(TextBlock.OpacityProperty, 0.75); t2.SetValue(TextBlock.MaxWidthProperty, 540.0);
        sp.AppendChild(t1); sp.AppendChild(t2); tpl.VisualTree = sp; subject.CellTemplate = tpl;
        view.Columns.Add(subject);
        _results.View = view;
        _results.MouseDoubleClick += (_, _) => { if (_results.SelectedItem?.GetType().GetProperty("Mail")?.GetValue(_results.SelectedItem) is MailHeader m) _open(m); };
        _results.ToolTip = L.T("Çift tık: maili aç");

        var foot = new DockPanel { Margin = new Thickness(16, 6, 16, 12) };
        var close = new Button { Content = L.T("Kapat"), MinWidth = 90, Height = 30, IsCancel = true };
        close.SetResourceReference(StyleProperty, "RibbonCmd"); close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); foot.Children.Add(close);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.TextWrapping = TextWrapping.Wrap; foot.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        var frame = new Border { Child = _results, BorderThickness = new Thickness(0, 1, 0, 1), Margin = new Thickness(16, 4, 16, 0) };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        root.Children.Add(frame);
        Content = root;

        _engine.SelectionChanged += (_, _) =>
        {
            if (_engine.SelectedItem is not Engine e) return;
            AppSettings.Shared.SemanticModel = e.Id; try { AppSettings.Shared.Save(); } catch { }
            OpenIndex();
        };
        Closed += (_, _) => _work?.Cancel();
        Loaded += async (_, _) => { await LoadEnginesAsync(); if (_query.Text.Length > 0) await SearchAsync(); _query.Focus(); };
    }

    async Task LoadEnginesAsync()
    {
        var engines = new List<Engine> { new(LocalEngine, L.T("Yerleşik (indirme gerektirmez)")) };
        var models = await OllamaEmbedder.InstalledModelsAsync();
        foreach (var m in models.OrderByDescending(OllamaEmbedder.IsEmbeddingModel))
            engines.Add(new("ollama:" + m, $"Ollama · {m}" + (OllamaEmbedder.IsEmbeddingModel(m) ? "  ★" : "  " + L.T("(sohbet modeli, zayıf)"))));
        _engine.ItemsSource = engines;
        var saved = AppSettings.Shared.SemanticModel ?? LocalEngine;
        _engine.SelectedItem = engines.FirstOrDefault(e => e.Id == saved) ?? engines[0];
        if (!models.Any(OllamaEmbedder.IsEmbeddingModel))
            _status.Text = models.Count == 0
                ? L.T("Ollama çalışmıyor; yerleşik motor kullanılıyor. Daha iyi anlam eşleşmesi için Ollama'ya bir gömme modeli kurun (ör. \"ollama pull bge-m3\").")
                : L.T("Ollama'da gömme modeli yok; yerleşik motor kullanılıyor. Daha iyi sonuç için: \"ollama pull bge-m3\" (yaklaşık 1,2 GB, Türkçe dahil çok dilli).");
    }

    void OpenIndex()
    {
        var id = (_engine.SelectedItem as Engine)?.Id ?? LocalEngine;
        IEmbedder embedder = id.StartsWith("ollama:") ? new OllamaEmbedder(id["ollama:".Length..]) : new LocalNgramEmbedder();
        _index = new SemanticIndex(null, embedder);
        var n = _index.Count;
        _status.Text = n == 0 ? L.T("Bu motor için dizin henüz yok; \"Dizini güncelle\" ile oluşturun (ilk sefer birkaç dakika sürebilir).") : L.F("Dizinde {0} mail var.", n);
    }

    /// <summary>Embeds new and changed mails (incremental).</summary>
    public async Task UpdateIndexAsync()
    {
        if (_index == null) OpenIndex();
        _work?.Cancel(); _work = new CancellationTokenSource();
        _update.IsEnabled = false; _progress.Visibility = Visibility.Visible; _progress.Value = 0;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => { _progress.Maximum = Math.Max(1, p.Total); _progress.Value = p.Done; _status.Text = L.F("Dizin güncelleniyor: {0} / {1}", p.Done, p.Total); });
            var index = _index!; var token = _work.Token;
            var n = await Task.Run(() => index.UpdateAsync(_cache, progress, token));
            _status.Text = L.F("Dizin hazır: {0} mail ({1} yeni / değişmiş).", index.Count, n);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = L.T("Dizin güncellenemedi: ") + ex.Message; }
        finally { _update.IsEnabled = true; _progress.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Runs the query (updating an empty index first).</summary>
    public async Task SearchAsync()
    {
        var q = _query.Text.Trim();
        if (q.Length == 0) return;
        if (_index == null) OpenIndex();
        if (_index!.Count == 0) await UpdateIndexAsync();
        try
        {
            var found = await _index.SearchAsync(q, _cache);
            // relevance relative to the best hit; weak matches (under a third of the best) are left out
            var best = found.Count > 0 ? Math.Max(1e-9, found[0].Score) : 1;
            Hits = found.Where(h => h.Score >= best * 0.33).ToList();
            _results.ItemsSource = Hits.Select(h => new
            {
                h.Mail, ScoreText = $"{h.Score / best * 100:0}%", Date = h.Mail.Date.LocalDateTime.ToString("g", L.Culture),
                From = h.Mail.FromName.Length > 0 ? h.Mail.FromName : h.Mail.FromAddress, Subject = h.Mail.SubjectDisplay, h.Snippet
            }).ToList();
            _status.Text = Hits.Count == 0 ? L.T("Eşleşen mail yok.") : L.F("{0} sonuç, en yakın anlam üstte.", Hits.Count);
        }
        catch (Exception ex) { _status.Text = L.T("Arama yapılamadı: ") + ex.Message; }
    }

    public void SetQuery(string q) => _query.Text = q;
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using MailStudio.Services;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MailStudio.Views.Editors;

/// <summary>
/// PowerPoint editor (Open XML SDK, MIT): edits the text boxes, titles, table cells and speaker notes of slides;
/// slides can be moved or deleted. Formatting, design, images and animations stay as in the file (the first format of each paragraph is kept).
/// The preview is produced by the converter; "Refresh preview" writes the edits to a temporary copy and redraws it. The attachment itself is never written.
/// </summary>
public sealed class SlideEditorWindow : Window
{
    sealed class TextItem { public string Key = ""; public string Label = ""; public string Text = ""; public string Original = ""; }
    sealed class SlideModel { public string RelId = ""; public int OriginalIndex; public bool Hidden; public string Title = ""; public List<TextItem> Items = new(); public TextItem? Notes; public bool Deleted; }

    readonly string _path, _displaySource; string? _savePath;
    readonly List<SlideModel> _slides = new();
    readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 420 };
    readonly StackPanel _fields = new();
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _previewNote = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    TechnicalWorker? _worker; string? _previewPdf; int _previewPages; bool _dirty; readonly CancellationTokenSource _life = new();
    readonly Dictionary<int, BitmapSource> _thumbs = new();

    public SlideEditorWindow(string path, string? displaySource = null)
    {
        _path = path; _displaySource = displaySource ?? path;
        Title = Path.GetFileName(_displaySource) + L.T(" — PowerPoint düzenleyici");
        Width = 1240; Height = 860; MinWidth = 760; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid();
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new() { Height = h });
        Content = root;
        root.Children.Add(BuildToolbar());
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(230) }); body.ColumnDefinitions.Add(new());
        body.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), Child = _list });
        var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20, 14, 20, 20) };
        var rs = new StackPanel(); right.Content = rs; rs.Children.Add(_previewNote);
        rs.Children.Add(new Border { Child = _preview, HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1), BorderBrush = ThemeManager.Brush("BorderStrongBrush"), Margin = new Thickness(0, 0, 0, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = .18, Direction = 270 } });
        rs.Children.Add(_fields);
        Grid.SetColumn(right, 1); body.Children.Add(right);
        var status = new Border { Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), Child = _status };
        Grid.SetRow(status, 2); root.Children.Add(status);
        _list.SelectionChanged += (_, _) => ShowSlide();
        PreviewKeyDown += (_, e) => { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) { _ = SaveAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; } };
        Load();
        Loaded += async (_, _) => await PreviewAsync(_path, original: true);
        Closing += OnClosing;
        Closed += (_, _) => { _life.Cancel(); _worker?.Dispose(); };
    }

    UIElement BuildToolbar()
    {
        var bar = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush") };
        var w = new WrapPanel(); bar.Child = w;
        void B(string glyph, string tip, Action a, string? text = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = sp, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(7, 3, 7, 3), MinHeight = 28 };
            if (TryFindResource("SubtleButton") is Style s) b.Style = s;
            b.Click += (_, _) => a(); w.Children.Add(b);
        }
        void Sep() => w.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), Background = ThemeManager.Brush("BorderSubtleBrush"), VerticalAlignment = VerticalAlignment.Center });
        B("", L.T("Kaydet (Ctrl+S)"), () => _ = SaveAsync(false), L.T("Kaydet"));
        B("", L.T("Farklı kaydet"), () => _ = SaveAsync(true));
        Sep();
        B("", L.T("Slaydı yukarı taşı"), () => Move(-1), L.T("Yukarı"));
        B("", L.T("Slaydı aşağı taşı"), () => Move(1), L.T("Aşağı"));
        B("", L.T("Slaydı sil"), DeleteSlide, L.T("Sil"));
        Sep();
        B("", L.T("Önizlemeyi yenile (düzenlemeler geçici kopyaya yazılıp Office ile yeniden çizilir)"), async () => await RefreshPreviewAsync(), L.T("Önizlemeyi yenile"));
        B("", L.T("PowerPoint'te aç (tasarım, resim, animasyon düzenleme)"), () => Shell(_savePath ?? _displaySource), L.T("PowerPoint'te aç"));
        return bar;
    }

    // ------------------------------------------------------------------ okuma

    void Load()
    {
        using var doc = PresentationDocument.Open(_path, false);
        var pres = doc.PresentationPart ?? throw new InvalidDataException(L.T("Sunu bölümü yok."));
        var ids = pres.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? new();
        int i = 0;
        foreach (var id in ids)
        {
            var rel = id.RelationshipId?.Value; if (rel == null || pres.GetPartById(rel) is not SlidePart sp) continue;
            var m = new SlideModel { RelId = rel, OriginalIndex = i++, Hidden = sp.Slide?.Show?.Value == false };
            foreach (var (key, label, text) in Texts(sp.Slide!)) m.Items.Add(new TextItem { Key = key, Label = label, Text = text, Original = text });
            m.Title = m.Items.FirstOrDefault(t => t.Label.StartsWith(L.T("Başlık")))?.Text.Split('\n')[0] ?? m.Items.FirstOrDefault()?.Text.Split('\n')[0] ?? "";
            if (sp.NotesSlidePart?.NotesSlide is { } ns)
            {
                var body = NotesBody(ns);
                if (body != null) { var t = ParagraphsText(body.TextBody!); m.Notes = new TextItem { Key = "notes", Label = L.T("Konuşmacı notları"), Text = t, Original = t }; }
            }
            _slides.Add(m);
        }
        RefreshList(0);
        _status.Text = L.F("{0} slayt. Metinleri sağda düzenleyin; tasarım ve resimler korunur. Kaydet ilk seferde yeni dosya sorar.", _slides.Count);
    }

    static P.Shape? NotesBody(P.NotesSlide ns) => ns.Descendants<P.Shape>().FirstOrDefault(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value == P.PlaceholderValues.Body && s.TextBody != null);

    /// <summary>Editable texts on a slide: shapes (including groups) and table cells. Key: "s{id}" or "t{id}:{row}:{column}".</summary>
    static IEnumerable<(string key, string label, string text)> Texts(P.Slide slide)
    {
        foreach (var shape in slide.Descendants<P.Shape>())
        {
            if (shape.TextBody == null) continue;
            var nv = shape.NonVisualShapeProperties; var id = nv?.NonVisualDrawingProperties?.Id?.Value ?? 0;
            var ph = nv?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
            if (ph == P.PlaceholderValues.SlideNumber || ph == P.PlaceholderValues.DateAndTime) continue;
            var isPh = nv?.ApplicationNonVisualDrawingProperties?.PlaceholderShape != null;
            var label = ph == P.PlaceholderValues.Title || ph == P.PlaceholderValues.CenteredTitle ? L.T("Başlık") : ph == P.PlaceholderValues.SubTitle ? L.T("Alt başlık") : ph == P.PlaceholderValues.Footer ? L.T("Alt bilgi")
                : isPh ? L.T("İçerik") : L.T("Metin kutusu: ") + (nv?.NonVisualDrawingProperties?.Name?.Value ?? "");
            yield return ($"s{id}", label, ParagraphsText(shape.TextBody));
        }
        foreach (var frame in slide.Descendants<P.GraphicFrame>())
        {
            var table = frame.Descendants<A.Table>().FirstOrDefault(); if (table == null) continue;
            var id = frame.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Id?.Value ?? 0;
            int r = 0;
            foreach (var row in table.Elements<A.TableRow>())
            {
                int c = 0;
                foreach (var cell in row.Elements<A.TableCell>())
                {
                    if (cell.TextBody != null && cell.HorizontalMerge?.Value != true && cell.VerticalMerge?.Value != true)
                        yield return ($"t{id}:{r}:{c}", L.F("Tablo {0}. satır {1}. sütun", r + 1, c + 1), ParagraphsText(cell.TextBody));
                    c++;
                }
                r++;
            }
        }
    }

    static string ParagraphsText(DocumentFormat.OpenXml.OpenXmlCompositeElement body) =>
        string.Join("\n", body.Elements<A.Paragraph>().Select(p => string.Concat(p.ChildElements.Select(e => e switch { A.Run r => r.Text?.Text ?? "", A.Field f => f.Text?.Text ?? "", A.Break => "\v", _ => "" }))));

    // ------------------------------------------------------------------ UI

    void RefreshList(int select)
    {
        _list.Items.Clear(); int n = 0;
        foreach (var s in _slides.Where(s => !s.Deleted))
        {
            n++;
            var sp = new StackPanel { Margin = new Thickness(6) };
            var img = new Image { Height = 110, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            if (_thumbs.TryGetValue(s.OriginalIndex, out var bmp)) img.Source = bmp;
            sp.Children.Add(new Border { BorderBrush = ThemeManager.Brush("BorderStrongBrush"), BorderThickness = new Thickness(1), Child = img, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 20, MinWidth = 40, Background = Brushes.White });
            var changed = s.Items.Any(i => i.Text != i.Original) || s.Notes is { } nt && nt.Text != nt.Original;
            sp.Children.Add(new TextBlock { Text = $"{n}. {(s.Title.Length > 0 ? s.Title : L.T("(başlıksız)"))}{(s.Hidden ? L.T("  · gizli") : "")}{(changed ? "  ●" : "")}", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 200 });
            _list.Items.Add(new ListBoxItem { Content = sp, Tag = s });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
    }

    SlideModel? Current => (_list.SelectedItem as ListBoxItem)?.Tag as SlideModel;

    void ShowSlide()
    {
        _fields.Children.Clear();
        var s = Current; if (s == null) return;
        _preview.Source = _thumbs.TryGetValue(s.OriginalIndex, out var bmp) ? bmp : null;
        _ = LoadPreviewPage(s);
        if (s.Items.Count == 0) _fields.Children.Add(new TextBlock { Text = L.T("Bu slaytta düzenlenebilir metin yok (yalnızca resim/şekil)."), Foreground = ThemeManager.Brush("TextSecondaryBrush") });
        foreach (var item in s.Items.Concat(s.Notes != null ? new[] { s.Notes } : Array.Empty<TextItem>()))
        {
            var it = item;
            _fields.Children.Add(new TextBlock { Text = it.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            var tb = new TextBox { Text = it.Text.Replace("\v", "\n"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = it.Label == L.T("Başlık") ? 30 : 56, Padding = new Thickness(6, 4, 6, 4), FontSize = it.Label == L.T("Başlık") ? 16 : 13.5 };
            tb.TextChanged += (_, _) =>
            {
                it.Text = tb.Text.Replace("\r\n", "\n");
                if (it.Label == L.T("Başlık")) s.Title = it.Text.Split('\n')[0];
                MarkDirty();
            };
            _fields.Children.Add(tb);
        }
    }

    void Move(int dir)
    {
        var s = Current; if (s == null) return;
        var visible = _slides.Where(x => !x.Deleted).ToList(); var i = visible.IndexOf(s); var j = i + dir;
        if (j < 0 || j >= visible.Count) return;
        var a = _slides.IndexOf(visible[i]); var b = _slides.IndexOf(visible[j]);
        (_slides[a], _slides[b]) = (_slides[b], _slides[a]);
        RefreshList(j); MarkDirty();
    }

    void DeleteSlide()
    {
        var s = Current; if (s == null) return;
        if (_slides.Count(x => !x.Deleted) <= 1) { _status.Text = L.T("Son slayt silinemez."); return; }
        if (MessageBox.Show(this, L.T("Bu slayt silinsin mi? (Kaydedene kadar dosya değişmez.)"), L.T("PowerPoint düzenleyici"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var i = _list.SelectedIndex; s.Deleted = true; RefreshList(i); MarkDirty();
    }

    void MarkDirty() { if (_dirty) return; _dirty = true; Title = "● " + Path.GetFileName(_savePath ?? _displaySource) + L.T(" — PowerPoint düzenleyici"); }

    // ------------------------------------------------------------------ preview (Office → PDF → worker)

    async Task PreviewAsync(string pptx, bool original)
    {
        if (OfficeConverter.AppFor(".pptx") is not { } app || !OfficeConverter.CanConvert(app)) { _previewNote.Text = L.T("Önizleme üretilemedi: belge dönüştürücü (office klasörü) eksik; metin düzenleme yine de çalışır."); return; }
        _previewNote.Text = L.T("Önizleme hazırlanıyor…");
        try
        {
            var pdf = await OfficeConverter.ToPdfAsync(pptx, _life.Token);
            _worker ??= new TechnicalWorker();
            var info = await Task.Run(() => _worker.CallAsync("pdfs_open", new { path = pdf }, _life.Token));
            _previewPdf = pdf; _previewPages = info.GetProperty("pages").GetInt32(); _thumbs.Clear();
            // Hidden slides are not in the PDF; the order follows the (saved) order in the file.
            var order = (original ? _slides.OrderBy(s => s.OriginalIndex) : _slides.Where(s => !s.Deleted).AsEnumerable()).Where(s => !s.Hidden).ToList();
            for (int k = 0; k < Math.Min(order.Count, _previewPages); k++)
            {
                var r = await Task.Run(() => _worker.CallAsync("pdfs_render", new { page = k, scale = 1.6, format = "jpeg" }, _life.Token));
                using var ms = new MemoryStream(Convert.FromBase64String(r.GetProperty("image").GetString()!));
                var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
                _thumbs[order[k].OriginalIndex] = bmp;
            }
            var sel = _list.SelectedIndex; RefreshList(sel < 0 ? 0 : sel);
            _previewNote.Text = original ? L.T("Önizleme dosyanın kaydedilmiş halidir; metin değişiklikleri için \"Önizlemeyi yenile\".") : L.T("Önizleme düzenlemelerinizle yenilendi.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _previewNote.Text = L.T("Önizleme üretilemedi: ") + ex.Message; }
    }

    Task LoadPreviewPage(SlideModel s) { if (_thumbs.TryGetValue(s.OriginalIndex, out var b)) _preview.Source = b; return Task.CompletedTask; }

    async Task RefreshPreviewAsync()
    {
        var tmpDir = Path.Combine(AppPaths.Root, "edit-copies", "onizleme-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, "onizleme.pptx");
        try { await Task.Run(() => Write(_path, tmp)); await PreviewAsync(tmp, original: false); }
        catch (Exception ex) { _status.Text = L.T("Önizleme yenilenemedi: ") + ex.Message; }
    }

    // ------------------------------------------------------------------ yazma

    void Write(string source, string dest)
    {
        var temp = dest + ".kayit.tmp";
        File.Copy(source, temp, true);
        try
        {
            using (var doc = PresentationDocument.Open(temp, true))
            {
                var pres = doc.PresentationPart!;
                var list = pres.Presentation!.SlideIdList!;
                var byRel = list.Elements<P.SlideId>().ToDictionary(x => x.RelationshipId!.Value!);
                foreach (var s in _slides)
                {
                    if (pres.GetPartById(s.RelId) is not SlidePart sp) continue;
                    if (s.Deleted) continue;
                    foreach (var it in s.Items.Where(i => i.Text != i.Original)) ApplyItem(sp.Slide!, it);
                    if (s.Notes is { } n && n.Text != n.Original && sp.NotesSlidePart?.NotesSlide is { } ns && NotesBody(ns)?.TextBody is { } nb) { SetParagraphs(nb, n.Text); ns.Save(); }
                    sp.Slide!.Save();
                }
                // order and deletion
                var ordered = _slides.Where(s => !s.Deleted).Select(s => byRel[s.RelId]).ToList();
                foreach (var s in _slides.Where(s => s.Deleted))
                {
                    var sid = byRel[s.RelId]; sid.Remove();
                    if (pres.GetPartById(s.RelId) is SlidePart dead) pres.DeletePart(dead);
                }
                foreach (var sid in ordered) sid.Remove();
                foreach (var sid in ordered) list.Append(sid);
                pres.Presentation.Save();
            }
            File.Move(temp, dest, true);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    static void ApplyItem(P.Slide slide, TextItem it)
    {
        if (it.Key.StartsWith('s') && uint.TryParse(it.Key[1..], out var id))
        {
            var shape = slide.Descendants<P.Shape>().FirstOrDefault(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Id?.Value == id);
            if (shape?.TextBody != null) SetParagraphs(shape.TextBody, it.Text);
        }
        else if (it.Key.StartsWith('t'))
        {
            var parts = it.Key[1..].Split(':'); if (parts.Length != 3 || !uint.TryParse(parts[0], out var fid)) return;
            var frame = slide.Descendants<P.GraphicFrame>().FirstOrDefault(f => f.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Id?.Value == fid);
            var row = frame?.Descendants<A.Table>().FirstOrDefault()?.Elements<A.TableRow>().ElementAtOrDefault(int.Parse(parts[1]));
            var cell = row?.Elements<A.TableCell>().ElementAtOrDefault(int.Parse(parts[2]));
            if (cell?.TextBody != null) SetParagraphs(cell.TextBody, it.Text);
        }
    }

    /// <summary>Adapts the number of paragraphs to the new lines; each paragraph's format (pPr) and its first run's format (rPr) are kept.</summary>
    static void SetParagraphs(DocumentFormat.OpenXml.OpenXmlCompositeElement body, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paras = body.Elements<A.Paragraph>().ToList();
        if (paras.Count == 0) { var np = new A.Paragraph(); body.Append(np); paras.Add(np); }
        var template = paras[^1];
        for (int i = 0; i < lines.Length; i++)
        {
            A.Paragraph p;
            if (i < paras.Count) p = paras[i];
            else { p = (A.Paragraph)template.CloneNode(true); paras[^1].InsertAfterSelf(p); paras.Add(p); }
            var rPr = p.Elements<A.Run>().FirstOrDefault()?.RunProperties?.CloneNode(true) as A.RunProperties
                      ?? (p.GetFirstChild<A.EndParagraphRunProperties>() is { } end ? FromEnd(end) : null);
            foreach (var el in p.ChildElements.Where(e => e is A.Run or A.Field or A.Break).ToList()) el.Remove();
            var segments = lines[i].Split('\v');
            var endPr = p.GetFirstChild<A.EndParagraphRunProperties>();
            for (int k = 0; k < segments.Length; k++)
            {
                if (k > 0) { var br = new A.Break(); if (rPr != null) br.RunProperties = (A.RunProperties)rPr.CloneNode(true); Insert(p, br, endPr); }
                if (segments[k].Length == 0) continue;
                var run = new A.Run(); if (rPr != null) run.RunProperties = (A.RunProperties)rPr.CloneNode(true);
                run.Text = new A.Text(segments[k]); Insert(p, run, endPr);
            }
        }
        for (int i = paras.Count - 1; i >= lines.Length; i--) paras[i].Remove();
    }

    static A.RunProperties FromEnd(A.EndParagraphRunProperties end)
    {
        var rp = new A.RunProperties();
        foreach (var attr in end.GetAttributes()) rp.SetAttribute(attr);
        foreach (var child in end.ChildElements) rp.Append(child.CloneNode(true));
        return rp;
    }

    static void Insert(A.Paragraph p, DocumentFormat.OpenXml.OpenXmlElement el, A.EndParagraphRunProperties? end) { if (end != null) end.InsertBeforeSelf(el); else p.Append(el); }

    public async Task<bool> SaveAsync(bool forceDialog, string? path = null)
    {
        path ??= _savePath;
        if (path == null || forceDialog)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = L.T("Sunuyu kaydet"), Filter = L.T("PowerPoint sunusu (*.pptx)|*.pptx"), FileName = Path.GetFileNameWithoutExtension(_displaySource) + ".pptx", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AddExtension = true };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_displaySource), StringComparison.OrdinalIgnoreCase) && path != _savePath)
        { MessageBox.Show(this, L.T("Ek dosyasının üzerine yazılmaz; farklı bir ad seçin."), L.T("PowerPoint düzenleyici")); return false; }
        try
        {
            _status.Text = L.T("Kaydediliyor…"); var dest = path;
            await Task.Run(() => Write(_path, dest));
            _savePath = path; _dirty = false; Title = Path.GetFileName(path) + L.T(" — PowerPoint düzenleyici");
            _status.Text = L.T("Kaydedildi: ") + path;
            return true;
        }
        catch (Exception ex) { _status.Text = L.T("Kaydedilemedi: ") + ex.Message; if (IsVisible) MessageBox.Show(this, L.T("Kaydedilemedi:\n") + ex.Message, L.T("PowerPoint düzenleyici"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    bool _closeAfterSave;
    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty || _closeAfterSave) return;
        var a = MessageBox.Show(this, L.T("Kaydedilmemiş değişiklikler var. Kaydedilsin mi?"), L.T("PowerPoint düzenleyici"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (a == MessageBoxResult.No) return;
        e.Cancel = true;
        if (a == MessageBoxResult.Yes && await SaveAsync(false)) { _closeAfterSave = true; Close(); }
    }

    static void Shell(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MailStudio.Models;
using MailStudio.Services.Ai;
using MailStudio.Services.Skills;
using MailStudio.Views;
using MailStudio.Views.Calendar;
using MailStudio.Views.Quotes;
using D = System.Drawing;

namespace MailStudio.Services;

/// <summary>
/// `MailStudio.exe --demo --showcase-linkedin2 <folder>`:
/// Drives MailStudio 1.8 for the v2 LinkedIn product film.
/// Captures off-screen at 1.5x resolution (2880x1620), logs exact element rectangles,
/// cursor coordinates and click events to events.json, dismisses ReminderWindow,
/// and synchronizes beat actions with timing.json for 1.0 playback rate.
/// </summary>
public static class ShowcaseLinkedIn2
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;

    public const int W = 1920, H = 1080;
    public const double CaptureScale = 1.5;

    static string _out = "";
    static readonly List<string> Log = new();

    // ---------- recorder with coordinate logging ----------
    sealed class Recorder
    {
        public Window? Target;
        public Point Cursor = new(W * 0.5, H * 0.5); // DIPs
        string _dir = "";
        int _n;
        int _titleHeight;
        readonly Stopwatch _sw = new();
        DispatcherTimer? _t;
        readonly List<string> _lines = new();
        readonly List<Task> _saves = new();
        readonly List<object> _events = new();
        static readonly D.Imaging.ImageCodecInfo Jpeg = D.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == D.Imaging.ImageFormat.Jpeg.Guid);

        public void Start(Window w, string scene)
        {
            Stop();
            DismissReminders();
            Target = w;
            _dir = Path.Combine(_out, scene);
            Directory.CreateDirectory(_dir);
            _n = 0;
            _lines.Clear();
            _events.Clear();
            _sw.Restart();

            var dpi = VisualTreeHelper.GetDpi(w);
            _titleHeight = (int)Math.Round(31 * dpi.DpiScaleY * CaptureScale);

            _t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            _t.Tick += (_, _) =>
            {
                try { Capture(); }
                catch (Exception ex) { Log.Add("capture skipped: " + ex.Message); }
            };
            _t.Start();
        }

        public void Stop()
        {
            if (_t == null) return;
            _t.Stop();
            _t = null;
            try { Capture(); } catch { }
            Task.WaitAll(_saves.ToArray());
            _saves.Clear();

            File.WriteAllLines(Path.Combine(_dir, "frames.txt"), _lines);
            var evtDoc = new
            {
                window = new { w = (int)(W * CaptureScale), h = (int)(H * CaptureScale), scale = CaptureScale },
                events = _events
            };
            File.WriteAllText(Path.Combine(_dir, "events.json"), JsonSerializer.Serialize(evtDoc, new JsonSerializerOptions { WriteIndented = true }));
            Log.Add($"{Path.GetFileName(_dir)}: {_n} frames, {_sw.ElapsedMilliseconds} ms");
        }

        void Capture()
        {
            var w = Target;
            if (w == null || !w.IsVisible) return;
            var (frame, clx, cly) = WindowCapture.Render(w, fresh: false, scale: CaptureScale);
            _titleHeight = cly;
            var file = $"f{_n++:00000}.jpg";
            _lines.Add($"{file}\t{_sw.ElapsedMilliseconds}");
            var path = Path.Combine(_dir, file);
            _saves.Add(Task.Run(() =>
            {
                using (frame)
                {
                    using var ep = new D.Imaging.EncoderParameters(1);
                    ep.Param[0] = new D.Imaging.EncoderParameter(D.Imaging.Encoder.Quality, 92L);
                    frame.Save(path, Jpeg, ep);
                }
            }));
        }

        public void LogTarget(string beat, string name, FrameworkElement el)
        {
            if (Target == null) return;
            var dpi = VisualTreeHelper.GetDpi(Target);
            try
            {
                var p = el.TransformToAncestor(Target).Transform(new Point(0, 0));
                int rx = (int)Math.Round(p.X * dpi.DpiScaleX * CaptureScale);
                int ry = _titleHeight + (int)Math.Round(p.Y * dpi.DpiScaleY * CaptureScale);
                int rw = (int)Math.Round(el.ActualWidth * dpi.DpiScaleX * CaptureScale);
                int rh = (int)Math.Round(el.ActualHeight * dpi.DpiScaleY * CaptureScale);
                _events.Add(new { t = _sw.ElapsedMilliseconds, type = "target", beat, name, rect = new[] { rx, ry, rw, rh } });
            }
            catch (Exception ex)
            {
                Log.Add($"LogTarget failed {beat}/{name}: {ex.Message}");
            }
        }

        public void LogCursor(Point dipPt)
        {
            if (Target == null) return;
            var dpi = VisualTreeHelper.GetDpi(Target);
            int cx = (int)Math.Round(dipPt.X * dpi.DpiScaleX * CaptureScale);
            int cy = _titleHeight + (int)Math.Round(dipPt.Y * dpi.DpiScaleY * CaptureScale);
            _events.Add(new { t = _sw.ElapsedMilliseconds, type = "cursor", x = cx, y = cy });
        }

        public void LogClick(Point dipPt)
        {
            if (Target == null) return;
            var dpi = VisualTreeHelper.GetDpi(Target);
            int cx = (int)Math.Round(dipPt.X * dpi.DpiScaleX * CaptureScale);
            int cy = _titleHeight + (int)Math.Round(dipPt.Y * dpi.DpiScaleY * CaptureScale);
            _events.Add(new { t = _sw.ElapsedMilliseconds, type = "click", x = cx, y = cy });
        }

        public void LogHold(string beat, int ms)
        {
            _events.Add(new { t = _sw.ElapsedMilliseconds, type = "hold", beat, ms });
        }
    }

    static readonly Recorder Rec = new();

    static void DismissReminders()
    {
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(x => x.GetType().Name == "ReminderWindow").ToList())
        {
            try { w.Close(); } catch { }
        }
    }

    static async Task Idle(int ms = 0)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (ms > 0) await Task.Delay(ms);
    }

    static void Place(Window w)
    {
        w.WindowState = WindowState.Normal;
        w.Left = 0;
        w.Top = 0;
        w.Width = W;
        w.Height = H;
    }

    static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        var q = new Queue<DependencyObject>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var d = q.Dequeue();
            yield return d;
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) q.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
    }

    static string TextOf(DependencyObject d) => d switch
    {
        ContentControl { Content: string s } => s,
        ContentControl c => string.Join(" ", Visuals(c).OfType<TextBlock>().Select(t => t.Text)),
        _ => ""
    };

    static T? FindEl<T>(Window w, Func<T, bool> ok) where T : FrameworkElement =>
        Visuals(w).OfType<T>().FirstOrDefault(e => e.IsVisible && e.ActualWidth > 0 && ok(e));

    static ButtonBase? Btn(Window w, string text) =>
        FindEl<ButtonBase>(w, b => TextOf(b).Contains(text, StringComparison.OrdinalIgnoreCase));

    static Point Center(Window w, FrameworkElement e, double fx = 0.5, double fy = 0.5)
    {
        try { return e.TransformToAncestor(w).Transform(new Point(e.ActualWidth * fx, e.ActualHeight * fy)); }
        catch { return Rec.Cursor; }
    }

    static async Task Move(Point to, int ms = 500)
    {
        var from = Rec.Cursor;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var t = sw.ElapsedMilliseconds / (double)ms;
            t = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            Rec.Cursor = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
            Rec.LogCursor(Rec.Cursor);
            await Task.Delay(25);
        }
        Rec.Cursor = to;
        Rec.LogCursor(to);
    }

    static async Task Move(FrameworkElement e, int ms = 500, double fx = 0.5, double fy = 0.5) =>
        await Move(Center(Rec.Target!, e, fx, fy), ms);

    static async Task Click(ButtonBase? b, int moveMs = 500)
    {
        if (b == null) return;
        await Move(b, moveMs);
        Rec.LogClick(Rec.Cursor);
        await Task.Delay(100);
        if (b is ToggleButton tb) tb.IsChecked = !(tb.IsChecked == true);
        else b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
        await Task.Delay(200);
    }

    static async Task<T> WaitNew<T>(HashSet<Window> before, Func<Window, bool>? ok = null, int seconds = 60) where T : Window
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            DismissReminders();
            var w = Application.Current.Windows.OfType<T>().LastOrDefault(x => x.IsVisible && !before.Contains(x) && (ok?.Invoke(x) ?? true));
            if (w != null) return w;
            await Task.Delay(100);
        }
        throw new TimeoutException(typeof(T).Name);
    }

    static async Task Until(Func<bool> ok, int seconds = 45)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!ok())
        {
            DismissReminders();
            if (DateTime.UtcNow > end) throw new TimeoutException("wait condition timed out");
            await Task.Delay(100);
        }
    }

    static void ForceClose(ComposeWindow c)
    {
        try { typeof(ComposeWindow).GetField("_confirmClose", Any)?.SetValue(c, (Func<MessageBoxResult>)(() => MessageBoxResult.No)); } catch { }
        try { c.Close(); } catch { }
    }

    // ---------- Main Runner ----------
    public static async Task RunAsync(MainWindow main, string outDir)
    {
        _out = outDir;
        Directory.CreateDirectory(_out);
        AppSettings.Shared.UndoSendSeconds = 10;

        try
        {
            DismissReminders();
            Place(main);
            await Task.Delay(2000);
            var list = (ListBox)main.FindName("MessageList");
            await Until(() => list.Items.Count >= 9);

            var cache = Field<MailCache>(main, "_cache");
            var all = cache.GetHeaders(DemoData.AccountId, "INBOX", 200);

            MailHeader Msg(uint uid) => list.Items.OfType<MailHeader>().First(m => m.Uid == uid);
            ListBoxItem? Row(uint uid) => list.ItemContainerGenerator.ContainerFromItem(Msg(uid)) as ListBoxItem;
            MailHeader With(string file) => all.First(h => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).Any(a => a.FileName == file));
            AttachmentInfo Att(MailHeader h, string file) => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).First(a => a.FileName == file);
            async Task<string> Download(MailHeader h, string file) => (await (Task<string?>)Call(main, "DownloadAttachmentAsync", h, Att(h, file))!)!;

            async Task<Window> OpenChip(uint uid, string file, Func<Window, bool>? ready = null)
            {
                DismissReminders();
                if (list.SelectedItem != Msg(uid)) { list.SelectedItem = Msg(uid); await Idle(1000); }
                var chip = FindEl<ButtonBase>(main, b => b.Tag is AttachmentInfo a && a.FileName == file);
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(chip, 600);
                var end = DateTime.UtcNow.AddSeconds(90);
                Window? found = null;
                int stable = 0;
                while (DateTime.UtcNow < end)
                {
                    DismissReminders();
                    await Task.Delay(400);
                    var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsVisible && !before.Contains(x));
                    if (w != null && w == found && (ready?.Invoke(w) ?? true)) { if (++stable >= 2) break; } else stable = 0;
                    found = w;
                }
                if (found == null) throw new TimeoutException(file);
                Place(found);
                await Idle(1200);
                return found;
            }

            async Task<Window> OpenOfficeChip(uint uid, string file)
            {
                DismissReminders();
                if (list.SelectedItem != Msg(uid)) { list.SelectedItem = Msg(uid); await Idle(1000); }
                var chip = FindEl<ButtonBase>(main, b => b.Tag is AttachmentInfo a && a.FileName == file);
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(chip, 600);
                var end = DateTime.UtcNow.AddSeconds(90);
                Window? found = null;
                while (DateTime.UtcNow < end)
                {
                    DismissReminders();
                    await Task.Delay(400);
                    var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsVisible && !before.Contains(x) && x.GetType().Name == "PdfAttachmentWindow");
                    if (w != null) { found = w; break; }
                }
                if (found == null)
                {
                    found = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsVisible && !before.Contains(x));
                }
                if (found == null) throw new TimeoutException(file);
                Place(found);
                await Idle(1200);
                return found;
            }

            // ==========================================
            // C01: Cold open – Inbox (5.6s)
            // ==========================================
            DismissReminders();
            list.SelectedItem = Msg(2);
            await Idle(1000);
            Rec.Start(main, "C01-inbox");
            Rec.Cursor = new Point(W * 0.45, H * 0.25);
            if (Row(1) is { } qRow1)
            {
                Rec.LogTarget("C01b1", "quotation-row", qRow1);
                await Move(Center(main, qRow1, 0.4, 0.5), 900);
                Rec.LogHold("C01b1", 1000);
                await Task.Delay(1100);

                Rec.LogTarget("C01b2", "quotation-row", qRow1);
                Rec.LogClick(Rec.Cursor);
                list.SelectedItem = Msg(1);
                Rec.LogHold("C01b2", 1200);
                await Task.Delay(2600);
            }
            Rec.Stop();

            // ==========================================
            // C02: AI Summarize & Reply (6.3s)
            // ==========================================
            DismissReminders();
            Rec.Start(main, "C02-ai");
            await Task.Delay(300);
            var sumBtn = Btn(main, L.T("AI Özet"));
            if (sumBtn != null)
            {
                await Click(sumBtn, 650);
                await Task.Delay(2200); // streaming summary
                var aiText = FindEl<TextBlock>(main, t => t.Text.Contains("flange", StringComparison.OrdinalIgnoreCase) || t.Text.Length > 80);
                if (aiText != null) Rec.LogTarget("C02b1", "ai-summary-text", aiText);
                Rec.LogHold("C02b1", 1200);
                await Task.Delay(1300);
            }
            var draftBtn = Btn(main, L.T("Cevap taslağı"));
            if (draftBtn != null)
            {
                await Click(draftBtn, 600);
                await Task.Delay(1800);
                var useBtn = Visuals(main).OfType<ButtonBase>().LastOrDefault(b => b.IsVisible && TextOf(b).Contains(ChatMessage.UseAsReplyLabel));
                if (useBtn != null)
                {
                    await Click(useBtn, 600);
                    await Task.Delay(600);
                    var compBody = FindEl<TextBox>(main, t => t.Name == "BodyBox" || t.IsVisible && t.ActualHeight > 100);
                    if (compBody != null) Rec.LogTarget("C02b2", "composer-body", compBody);
                    Rec.LogHold("C02b2", 1400);
                    await Task.Delay(1500);
                }
            }
            Rec.Stop();
            if (main.GetType().GetField("_inlineCompose", Any)?.GetValue(main) is { } ic) Call(ic, "CloseForTransfer");
            await Idle(600);

            // ==========================================
            // C03: Skills & OCR (8.1s)
            // ==========================================
            DismissReminders();
            list.SelectedItem = Msg(1);
            await Idle(600);
            Rec.Start(main, "C03-skills-ocr");
            var skillQuoteBtn = Btn(main, L.T("Teklif talebi analizi")) ?? Btn(main, "Quotation request");
            if (skillQuoteBtn != null)
            {
                Rec.LogTarget("C03b1", "skill-button-quote", skillQuoteBtn);
                await Click(skillQuoteBtn, 650);
                Rec.LogHold("C03b1", 900);
                await Task.Delay(800);
            }
            var statusLine = FindEl<TextBlock>(main, t => t.Text.Contains("OCR") || t.Text.Contains("okunuyor") || t.Text.Contains("Reading"));
            if (statusLine != null)
            {
                Rec.LogTarget("C03b2", "skill-status-line", statusLine);
                Rec.LogHold("C03b2", 1000);
                await Task.Delay(1800);
            }
            var analysisNote = FindEl<Border>(main, b => b.Child is TextBlock tb && (tb.Text.Contains("DN100") || tb.Text.Contains("flange") || tb.Text.Contains("PN16")));
            if (analysisNote != null)
            {
                Rec.LogTarget("C03b3", "skill-analysis-note", analysisNote);
                Rec.LogHold("C03b3", 1500);
                await Task.Delay(2600);
            }
            else await Task.Delay(2500);
            Rec.Stop();

            // ==========================================
            // C04: Revision Compare (14.2s)
            // ==========================================
            DismissReminders();
            var revC = With("flange-DN100-revC.dxf");
            list.SelectedItem = list.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == revC.Uid);
            await Idle(1200);

            var newDxfPath = await Download(revC, "flange-DN100-revC.dxf");
            var beforeChooser = Application.Current.Windows.OfType<Window>().ToHashSet();
            var pickTask = main.Dispatcher.InvokeAsync(() => (Task<string?>)Call(main, "PickOlderRevisionAsync", main, newDxfPath)!);
            var chooser = await WaitNew<Window>(beforeChooser, w => w.Title.Contains("revizyon") || w.Title.Contains("revision"), 20);
            await Idle(600);

            Rec.Start(chooser, "C04-revdiff");
            var cmpBtn = Btn(chooser, L.T("Karşılaştır")) ?? Btn(chooser, "Compare");
            if (cmpBtn != null)
            {
                Rec.LogTarget("C04b1", "revdiff-compare-btn", cmpBtn);
                await Move(cmpBtn, 650);
                Rec.LogHold("C04b1", 1000);
                await Task.Delay(1000);
                Rec.LogClick(Rec.Cursor);
                cmpBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cmpBtn));
            }
            var oldDxfPath = await await pickTask.Task;

            var beforeDiff = Application.Current.Windows.OfType<Window>().ToHashSet();
            _ = (Task)Call(main, "ShowRevisionDiffAsync", oldDxfPath!, newDxfPath, revC)!;
            var diff = await WaitNew<RevisionDiffWindow>(beforeDiff, null, 40);
            Place(diff);
            await Idle(1200);
            Rec.Target = diff;

            var diffList = FindEl<ListView>(diff, _ => true);
            if (diffList != null)
            {
                // Hole diameter change row
                var rowHole = Visuals(diffList).OfType<ListViewItem>().FirstOrDefault(it => TextOf(it).Contains("Ø20") || TextOf(it).Contains("18"));
                if (rowHole != null)
                {
                    Rec.LogTarget("C04b2", "diff-row-hole-diam", rowHole);
                    await Move(Center(diff, rowHole, 0.4, 0.5), 600);
                    rowHole.IsSelected = true;
                    Rec.LogHold("C04b2", 1200);
                    await Task.Delay(2600);
                }
                // Roughness change row
                var rowRa = Visuals(diffList).OfType<ListViewItem>().FirstOrDefault(it => TextOf(it).Contains("1.6") || TextOf(it).Contains("3.2"));
                if (rowRa != null)
                {
                    Rec.LogTarget("C04b3", "diff-row-roughness", rowRa);
                    await Move(Center(diff, rowRa, 0.4, 0.5), 550);
                    rowRa.IsSelected = true;
                    Rec.LogHold("C04b3", 1100);
                    await Task.Delay(2000);
                }
            }
            var addReplyBtn = Btn(diff, L.T("Farkı Yanıta Ekle")) ?? Btn(diff, "Add Diff to Reply");
            if (addReplyBtn != null)
            {
                Rec.LogTarget("C04b4", "diff-add-to-reply-btn", addReplyBtn);
                await Click(addReplyBtn, 600);
                Rec.LogHold("C04b4", 1200);
                await Task.Delay(1800);
            }
            Rec.Stop();
            diff.Close();
            foreach (var c in main.OwnedWindows.OfType<ComposeWindow>().ToList()) ForceClose(c);
            if (Field<ComposeWindow?>(main, "_inlineCompose") is { } ic2) ForceClose(ic2);
            await Idle(600);

            // ==========================================
            // C05: STEP B-rep, Stock & Weight (12.9s)
            // ==========================================
            DismissReminders();
            list.SelectedItem = Msg(2);
            await Idle(800);
            var step = await OpenChip(2, "bracket-revC.stp", w => w.GetType().GetField("_loaded", Any)?.GetValue(w) is true);
            await Idle(1200);
            Rec.Start(step, "C05-step");

            var viewport = FindEl<FrameworkElement>(step, e => e.GetType().Name == "Viewport3D");
            if (viewport != null)
            {
                Rec.LogTarget("C05b1", "step-viewport", viewport);
                await Move(Center(step, viewport, 0.5, 0.5), 600);
                for (int i = 0; i < 28; i++)
                {
                    Call(step, "Orbit", 4.5, i < 14 ? 0.4 : -0.4);
                    await Task.Delay(40);
                }
                Rec.LogHold("C05b1", 1000);
            }

            // Click bore & display tag
            var boreTag = FindEl<FrameworkElement>(step, e => e.Name == "_labelCanvas" || TextOf(e).Contains("25"));
            if (boreTag != null)
            {
                Rec.LogTarget("C05b2", "step-bore-tag", boreTag);
                Rec.LogClick(Rec.Cursor);
                Rec.LogHold("C05b2", 1100);
                await Task.Delay(1800);
            }

            // Stock tab
            var tabs = FindEl<TabControl>(step, _ => true);
            if (tabs != null)
            {
                tabs.SelectedIndex = 1; // Bounding Box / Stock
                await Idle(600);
                Rec.LogTarget("C05b3", "step-stock-tab", tabs);
                Rec.LogHold("C05b3", 1100);
                await Task.Delay(1600);

                var massInfo = FindEl<TextBlock>(step, t => t.Text.Contains("Talaş") || t.Text.Contains("Chips") || t.Text.Contains("Parça") || t.Text.Contains("Part"));
                if (massInfo != null)
                {
                    Rec.LogTarget("C05b4", "step-chips-readout", massInfo);
                    await Move(Center(step, massInfo, 0.4, 0.5), 650);
                    Rec.LogHold("C05b4", 1400);
                    await Task.Delay(2600);
                }
                else await Task.Delay(2000);
            }
            Rec.Stop();
            step.Close();
            await Idle(600);

            // ==========================================
            // C06: DXF Layers & Measure (10.0s)
            // ==========================================
            DismissReminders();
            var cad = await OpenChip(1, "flange-DN100-revB.dxf");
            await Idle(1500);
            Rec.Start(cad, "C06-dxf");

            var hatchCheck = FindEl<CheckBox>(cad, c => TextOf(c).Contains("HATCH"));
            if (hatchCheck != null)
            {
                Rec.LogTarget("C06b1", "dxf-layer-hatch", hatchCheck);
                await Click(hatchCheck, 600);
                await Task.Delay(400);
                await Click(hatchCheck, 400);
                Rec.LogHold("C06b1", 1000);
                await Task.Delay(800);
            }

            // Measure snap
            var measureBtn = FindEl<ToggleButton>(cad, b => TextOf(b).Contains("Ölç") || TextOf(b).Contains("Measure"));
            if (measureBtn != null)
            {
                await Click(measureBtn, 550);
                var cadCanvas = FindEl<FrameworkElement>(cad, e => e.GetType().Name.Contains("CadCanvas") || e.Name == "_canvas");
                if (cadCanvas != null)
                {
                    Rec.LogTarget("C06b2", "dxf-snap-point", cadCanvas);
                    await Move(Center(cad, cadCanvas, 0.35, 0.5), 600);
                    Rec.LogClick(Rec.Cursor);
                    await Task.Delay(300);
                    await Move(Center(cad, cadCanvas, 0.65, 0.5), 700);
                    Rec.LogClick(Rec.Cursor);
                    Rec.LogHold("C06b2", 1200);
                    await Task.Delay(1000);
                }
            }
            var statusCad = FindEl<TextBlock>(cad, t => t.Text.Contains("220") || t.Text.Contains("Mesafe") || t.Text.Contains("Distance"));
            if (statusCad != null)
            {
                Rec.LogTarget("C06b3", "dxf-measure-readout", statusCad);
                Rec.LogHold("C06b3", 1100);
                await Task.Delay(2200);
            }
            else await Task.Delay(2000);
            Rec.Stop();
            cad.Close();
            await Idle(600);

            // ==========================================
            // C07: PDF Studio OCR & Markup (9.8s)
            // ==========================================
            DismissReminders();
            var pdf = await OpenChip(1, "flange-DN100.pdf");
            await Idle(1500);
            Rec.Start(pdf, "C07-pdf");

            var ocrBtn = Btn(pdf, "OCR");
            if (ocrBtn != null)
            {
                Rec.LogTarget("C07b1", "pdf-ocr-btn", ocrBtn);
                await Click(ocrBtn, 600);
                Rec.LogHold("C07b1", 1000);
                await Task.Delay(1800);
            }

            var pdfView = FindEl<FrameworkElement>(pdf, e => e.Name == "_image" || e.GetType().Name.Contains("Image"));
            if (pdfView != null)
            {
                Rec.LogTarget("C07b2", "pdf-highlight-box", pdfView);
                await Move(Center(pdf, pdfView, 0.55, 0.45), 650);
                Rec.LogClick(Rec.Cursor);
                Rec.LogHold("C07b2", 1200);
                await Task.Delay(1600);
            }

            var askBtn = Btn(pdf, L.T("Sor")) ?? Btn(pdf, "Ask");
            if (askBtn != null)
            {
                await Click(askBtn, 600);
                await Task.Delay(1200);
                var askAnswer = FindEl<TextBlock>(pdf, t => t.Text.Contains("Sayfa") || t.Text.Contains("Page") || t.Text.Length > 40);
                if (askAnswer != null) Rec.LogTarget("C07b3", "pdf-ask-answer", askAnswer);
                Rec.LogHold("C07b3", 1400);
                await Task.Delay(2400);
            }
            else await Task.Delay(2000);
            Rec.Stop();
            pdf.Close();
            await Idle(600);

            // ==========================================
            // C08: Excel Update & Diff Preview (8.6s)
            // ==========================================
            DismissReminders();
            list.SelectedItem = Msg(3); // Delivery schedule
            await Idle(800);
            Rec.Start(main, "C08-excel-diff");

            var excelSkill = Btn(main, L.T("Teslim planını güncelle")) ?? Btn(main, "delivery schedule");
            if (excelSkill != null)
            {
                Rec.LogTarget("C08b1", "skill-button-excel", excelSkill);
                await Click(excelSkill, 600);
                Rec.LogHold("C08b1", 1000);
                await Task.Delay(1400);
            }

            // Preview grid in ExcelPlanWindow or reading pane
            var excelWin = Application.Current.Windows.OfType<ExcelPlanWindow>().LastOrDefault();
            if (excelWin != null)
            {
                Place(excelWin);
                Rec.Target = excelWin;
                var previewGrid = FindEl<DataGrid>(excelWin, g => g.Columns.Count >= 4);
                if (previewGrid != null)
                {
                    Rec.LogTarget("C08b2", "excel-preview-grid", previewGrid);
                    Rec.LogHold("C08b2", 1300);
                    await Task.Delay(2200);
                }
                var saveNewBtn = Btn(excelWin, L.T("Yeni dosya")) ?? Btn(excelWin, "Save as new");
                if (saveNewBtn != null)
                {
                    Rec.LogTarget("C08b3", "excel-editor-grid", saveNewBtn);
                    await Move(saveNewBtn, 650);
                    Rec.LogHold("C08b3", 1300);
                    await Task.Delay(2000);
                }
                excelWin.Close();
            }
            else
            {
                await Task.Delay(3500);
            }
            Rec.Stop();

            // ==========================================
            // C09: Office, CNC & Archives (11.8s)
            // ==========================================
            DismissReminders();
            // 9a) Word FAI
            var docx = await OpenOfficeChip(4, "FAI-report-NW-1042.docx");
            await Idle(600);
            Rec.Start(docx, "C09a-word");
            Rec.LogTarget("C09b1", "word-viewer-doc", docx.Content as FrameworkElement ?? docx);
            await Move(new Point(W * 0.45, H * 0.4), 600);
            Rec.LogHold("C09b1", 900);
            await Task.Delay(2000);
            Rec.Stop();
            docx.Close();
            await Idle(500);

            // 9b) PowerPoint
            var pptx = await OpenOfficeChip(2, "kickoff-bracket-revC.pptx");
            await Idle(600);
            Rec.Start(pptx, "C09b-ppt");
            Rec.LogTarget("C09b2", "pptx-viewer-slide", pptx.Content as FrameworkElement ?? pptx);
            await Move(new Point(W * 0.35, H * 0.45), 600);
            Rec.LogHold("C09b2", 900);
            await Task.Delay(1800);
            Rec.Stop();
            pptx.Close();
            await Idle(500);

            // 9c) CNC G-code
            var nc = await OpenChip(5, "OP20.nc");
            await Idle(1000);
            Rec.Start(nc, "C09c-cnc");
            var cncTools = FindEl<TextBlock>(nc, t => t.Text.Contains("T1") || t.Text.Contains("T2"));
            if (cncTools != null) Rec.LogTarget("C09b3", "cnc-tools-header", cncTools);
            else Rec.LogTarget("C09b3", "cnc-tools-header", nc.Content as FrameworkElement ?? nc);
            await Move(new Point(W * 0.38, H * 0.35), 600);
            Rec.LogHold("C09b3", 1000);
            await Task.Delay(2000);
            Rec.Stop();
            nc.Close();
            await Idle(500);

            // 9d) RAR archive
            list.SelectedItem = Msg(11);
            await Idle(600);
            var rarWin = await OpenChip(11, "NW-1042-drawings.rar");
            await Idle(1000);
            Rec.Start(rarWin, "C09d-rar");
            var arcList = FindEl<ListView>(rarWin, _ => true);
            if (arcList?.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem it0)
            {
                Rec.LogTarget("C09b4", "archive-item-pdf", it0);
                await Move(Center(rarWin, it0, 0.35, 0.5), 650);
                Rec.LogClick(Rec.Cursor);
                it0.IsSelected = true;
                Rec.LogHold("C09b4", 1200);
                await Task.Delay(2600);
            }
            else await Task.Delay(2000);
            Rec.Stop();
            rarWin.Close();
            await Idle(600);

            // ==========================================
            // C10: Safety & Calendar (11.6s)
            // ==========================================
            DismissReminders();
            var anna = all.First(h => h.FromAddress.StartsWith("anna", StringComparison.OrdinalIgnoreCase));
            var beforeSafeComp = Application.Current.Windows.OfType<Window>().ToHashSet();
            Call(main, "OpenCompose", new ComposeRequest
            {
                To = anna.FromAddress,
                Subject = L.T("NW-1042 rev C quotation"),
                Body = L.T("Hello Anna,\nPlease find attached the quotation.")
            }, DemoData.AccountId, true);
            var compSafe = await WaitNew<ComposeWindow>(beforeSafeComp, null, 25);
            Place(compSafe);
            await Until(() => compSafe.IsEditorInitialized, 30);
            await Idle(1000);

            Rec.Start(compSafe, "C10-safety-cal");
            var sendBtn = Btn(compSafe, L.T("Gönder")) ?? Btn(compSafe, "Send");
            var beforeMissing = Application.Current.Windows.OfType<Window>().ToHashSet();
            await Click(sendBtn, 600);

            var missingDlg = await WaitNew<Window>(beforeMissing, w => w.Title.Contains("unutulmuş") || w.Title.Contains("missing"), 15);
            await Idle(600);
            Rec.Target = missingDlg;
            Rec.LogTarget("C10b1", "missing-attachment-dialog", missingDlg.Content as FrameworkElement ?? missingDlg);
            Rec.LogHold("C10b1", 1300);
            await Task.Delay(2200);
            missingDlg.Close();
            ForceClose(compSafe);
            await Idle(600);

            // Undo send
            Rec.Target = main;
            main.Dispatcher.Invoke(() =>
            {
                var bar = (FrameworkElement)main.FindName("UndoSendBar");
                var txt = (TextBlock)main.FindName("UndoSendText");
                if (bar != null) bar.Visibility = Visibility.Visible;
                if (txt != null) txt.Text = "Sending in 10s...";
            });
            await Idle(300);
            var undoBar = (FrameworkElement)main.FindName("UndoSendBar");
            if (undoBar != null)
            {
                undoBar.UpdateLayout();
                Rec.LogTarget("C10b2", "undo-bar-btn", undoBar);
                var undoBtn = (ButtonBase)main.FindName("UndoSendButton");
                if (undoBtn != null) await Click(undoBtn, 650);
                Rec.LogHold("C10b2", 1100);
                await Task.Delay(1600);
                main.Dispatcher.Invoke(() =>
                {
                    var bar = (FrameworkElement)main.FindName("UndoSendBar");
                    if (bar != null) bar.Visibility = Visibility.Collapsed;
                });
            }

            // Outbox
            var beforeOutbox = Application.Current.Windows.OfType<Window>().ToHashSet();
            _ = main.Dispatcher.BeginInvoke(() => Call(main, "ShowOutboxWindow"));
            var ow = await WaitNew<OutboxWindow>(beforeOutbox, null, 15);
            await Idle(600);
            Rec.Target = ow;
            Rec.LogTarget("C10b3", "outbox-list", ow.Content as FrameworkElement ?? ow);
            Rec.LogHold("C10b3", 1000);
            await Task.Delay(1600);
            ow.Close();
            await Idle(500);

            // Calendar invitation accept
            Rec.Target = main;
            main.SwitchModule(PimModule.Calendar);
            await Idle(800);
            var calView = Field<CalendarView?>(main, "_calendarView");
            calView?.SetMode(CalendarViewMode.Week);
            await Idle(600);
            Rec.LogTarget("C10b4", "calendar-week-grid", calView ?? (FrameworkElement)main);
            await Move(new Point(W * 0.5, H * 0.45), 650);
            Rec.LogHold("C10b4", 1400);
            await Task.Delay(2400);
            main.SwitchModule(PimModule.Mail);
            Rec.Stop();

            // ==========================================
            // C11: Quote Desk, Dark Theme & End (14.6s)
            // ==========================================
            DismissReminders();
            main.SwitchModule(PimModule.Quotes);
            await Idle(1200);
            Rec.Start(main, "C11-desk-dark-end");

            var desk = Field<QuoteDeskView?>(main, "_quoteDeskView")!;
            var wonBtn = Btn(main, L.T("Kazanıldı")) ?? Btn(main, "Won");
            if (wonBtn != null)
            {
                Rec.LogTarget("C11b1", "quote-status-won", wonBtn);
                await Click(wonBtn, 650);
                Rec.LogHold("C11b1", 1200);
                await Task.Delay(2600);
            }
            main.SwitchModule(PimModule.Mail);
            await Idle(600);

            // Contact card Anna
            try
            {
                var beforeContact = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = main.Dispatcher.BeginInvoke(() => main.OpenContactCard("Anna Keller", "anna.keller@northwind-machining.example"));
                var cw = await WaitNew<Window>(beforeContact, null, 15);
                Place(cw);
                await Idle(600);
                Rec.Target = cw;
                var cardTabs = FindEl<TabControl>(cw, _ => true);
                Rec.LogTarget("C11b2", "contact-card-tabs", (FrameworkElement?)cardTabs ?? cw.Content as FrameworkElement ?? cw);
                await Move(new Point(W * 0.4, H * 0.35), 650);
                Rec.LogHold("C11b2", 1300);
                await Task.Delay(2600);
                cw.Close();
                await Idle(500);
            }
            catch (Exception ex) { Log.Add("Contact card skip: " + ex.Message); }

            // Dark theme
            Rec.Target = main;
            ((RadioButton)main.FindName("ThemeDark")).IsChecked = true;
            await Idle(600);
            Rec.LogTarget("C11b3", "dark-theme-inbox", main.Content as FrameworkElement ?? main);
            Rec.LogHold("C11b3", 1200);
            await Task.Delay(2200);

            // End card
            Rec.LogTarget("C11b4", "end-card-logo", main.Content as FrameworkElement ?? main);
            Rec.LogHold("C11b4", 2000);
            await Task.Delay(2800);
            Rec.Stop();

            Log.Add("OK LinkedIn2 Showcase Complete");
        }
        catch (Exception ex)
        {
            Log.Add("FAIL Showcase: " + ex);
        }

        Finish();
    }

    static void Finish()
    {
        try { Rec.Stop(); } catch { }
        File.WriteAllLines(Path.Combine(_out, "showcase-linkedin2.log"), Log);
        Environment.Exit(0);
    }
}

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MailStudio.Models;
using MailStudio.Views.Calendar;
using D = System.Drawing;

namespace MailStudio.Services;

/// <summary>
/// `MailStudio.exe --demo --showcase-video &lt;folder&gt;`: drives the demo mailbox like a user would (real button clicks,
/// real viewers) and records MailStudio's own windows at ~15 fps with a drawn mouse pointer. Only our window handles are
/// captured (PrintWindow) — nothing else on the screen. Each scene goes to &lt;folder&gt;\&lt;scene&gt;\ as JPEG frames plus
/// frames.txt (file, milliseconds) so the film can be assembled at a constant frame rate.
/// </summary>
public static class ShowcaseVideo
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    public const int W = 1680, H = 945;   // every recorded window gets the same size (16:9)

    static string _out = "";
    static readonly List<string> Log = new();

    // ---------- recorder ----------
    sealed class Recorder
    {
        public Window? Target;
        public Point Cursor = new(W * 0.6, H * 0.55);   // client DIPs
        public double Ripple = -1;                     // 0..1 while a click ripple is shown
        string _dir = ""; int _n; bool _offScreen; readonly Stopwatch _sw = new(); DispatcherTimer? _t;
        readonly List<string> _lines = new(); readonly List<Task> _saves = new();
        static readonly D.Imaging.ImageCodecInfo Jpeg = D.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == D.Imaging.ImageFormat.Jpeg.Guid);

        public void Start(Window w, string scene)
        {
            Stop();
            _offScreen = WindowCapture.OffScreen;
            Target = w; _dir = Path.Combine(_out, scene); Directory.CreateDirectory(_dir); _n = 0; _lines.Clear(); _sw.Restart();
            _t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
            _t.Tick += (_, _) => { try { Capture(); } catch (Exception ex) { Log.Add("capture skipped: " + ex.Message); } };
            _t.Start();
        }

        public void Stop()
        {
            if (_t == null) return;
            _t.Stop(); _t = null;
            try { Capture(); } catch { }
            Task.WaitAll(_saves.ToArray()); _saves.Clear();
            File.WriteAllLines(Path.Combine(_dir, "frames.txt"), _lines);
            Log.Add($"{Path.GetFileName(_dir)}: {_n} frames, {_sw.ElapsedMilliseconds} ms");
        }

        void Capture()
        {
            var w = Target; if (w == null || !w.IsVisible) return;
            var h = new WindowInteropHelper(w).Handle;
            var dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
            D.Bitmap frame; float cx, cy;
            if (_offScreen)
            {
                // Hidden desktop: WPF renders the window, the mail view comes from the last WebView2 capture
                var (rendered, clx, cly) = WindowCapture.Render(w, fresh: false);
                frame = rendered;
                cx = (float)(clx + Cursor.X * dpi); cy = (float)(cly + Cursor.Y * dpi);
            }
            else
            {
                GetWindowRect(h, out var r);
                var bmp = new D.Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), D.Imaging.PixelFormat.Format32bppArgb);
                using (var g = D.Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
                int ox = 0, oy = 0; frame = bmp;
                if (DwmGetWindowAttribute(h, 9, out var ef, Marshal.SizeOf<RECT>()) == 0)
                {
                    var crop = D.Rectangle.Intersect(new D.Rectangle(ef.L - r.L, ef.T - r.T, ef.R - ef.L, ef.B - ef.T), new D.Rectangle(0, 0, bmp.Width, bmp.Height));
                    if (crop.Width > 10 && crop.Height > 10) { frame = bmp.Clone(crop, bmp.PixelFormat); bmp.Dispose(); ox = r.L + crop.X; oy = r.T + crop.Y; }
                }
                var p = new POINT(); ClientToScreen(h, ref p);
                cx = (float)(p.X - ox + Cursor.X * dpi); cy = (float)(p.Y - oy + Cursor.Y * dpi);
            }
            var ripple = Ripple;
            var file = $"f{_n++:00000}.jpg";
            _lines.Add($"{file}\t{_sw.ElapsedMilliseconds}");
            var path = Path.Combine(_dir, file);
            _saves.Add(Task.Run(() =>
            {
                using (frame)
                {
                    using (var g = D.Graphics.FromImage(frame)) { g.SmoothingMode = D.Drawing2D.SmoothingMode.AntiAlias; DrawPointer(g, cx, cy, ripple, (float)dpi); }
                    using var ep = new D.Imaging.EncoderParameters(1);
                    ep.Param[0] = new D.Imaging.EncoderParameter(D.Imaging.Encoder.Quality, 94L);
                    frame.Save(path, Jpeg, ep);
                }
            }));
        }

        static void DrawPointer(D.Graphics g, float x, float y, double ripple, float s)
        {
            if (ripple >= 0)
            {
                var rad = (float)(8 + ripple * 26) * s;
                using var b = new D.SolidBrush(D.Color.FromArgb((int)(150 * (1 - ripple)), 15, 108, 189));
                g.FillEllipse(b, x - rad, y - rad, rad * 2, rad * 2);
            }
            D.PointF[] arrow = { new(0, 0), new(0, 17), new(4.2f, 13.2f), new(7.2f, 20f), new(10f, 18.8f), new(7f, 12.2f), new(12.4f, 12.2f) };
            var pts = arrow.Select(a => new D.PointF(x + a.X * 1.25f * s, y + a.Y * 1.25f * s)).ToArray();
            using var fill = new D.SolidBrush(D.Color.White);
            using var pen = new D.Pen(D.Color.Black, 1.4f * s) { LineJoin = D.Drawing2D.LineJoin.Round };
            g.FillPolygon(fill, pts); g.DrawPolygon(pen, pts);
        }
    }

    static readonly Recorder Rec = new();

    // ---------- helpers ----------
    static async Task Idle(int ms = 0)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (ms > 0) await Task.Delay(ms);
    }

    static void Place(Window w) { w.WindowState = WindowState.Normal; w.Left = 20; w.Top = 20; w.Width = W; w.Height = H; }

    static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        var q = new Queue<DependencyObject>(); q.Enqueue(root);
        while (q.Count > 0)
        {
            var d = q.Dequeue(); yield return d;
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

    static ButtonBase? Btn(Window w, string text) => FindEl<ButtonBase>(w, b => TextOf(b).Contains(text, StringComparison.OrdinalIgnoreCase));

    static Point Center(Window w, FrameworkElement e, double fx = 0.5, double fy = 0.5)
    {
        try { return e.TransformToAncestor(w).Transform(new Point(e.ActualWidth * fx, e.ActualHeight * fy)); }
        catch { return Rec.Cursor; }
    }

    /// <summary>Glides the drawn pointer to a point (ease in-out).</summary>
    static async Task Move(Point to, int ms = 550)
    {
        var from = Rec.Cursor; var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var t = sw.ElapsedMilliseconds / (double)ms; t = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            Rec.Cursor = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
            await Task.Delay(15);
        }
        Rec.Cursor = to;
    }

    static async Task Move(FrameworkElement e, int ms = 550) => await Move(Center(Rec.Target!, e), ms);

    static async Task ClickFx()
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 320) { Rec.Ripple = sw.ElapsedMilliseconds / 320.0; await Task.Delay(15); }
        Rec.Ripple = -1;
    }

    /// <summary>Moves to a button, shows the click and really clicks it.</summary>
    static async Task Click(ButtonBase? b, int moveMs = 550)
    {
        if (b == null) { Log.Add("button not found"); return; }
        await Move(b, moveMs);
        var fx = ClickFx();
        await Task.Delay(90);
        if (b is ToggleButton tb) tb.IsChecked = !(tb.IsChecked == true);
        else b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
        await fx;
    }

    static async Task<T> WaitNew<T>(HashSet<Window> before, Func<Window, bool>? ok = null, int seconds = 90) where T : Window
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var w = Application.Current.Windows.OfType<T>().LastOrDefault(x => x.IsVisible && !before.Contains(x) && (ok?.Invoke(x) ?? true));
            if (w != null) return w;
            await Task.Delay(150);
        }
        throw new TimeoutException(typeof(T).Name);
    }

    static async Task Until(Func<bool> ok, int seconds = 60)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!ok()) { if (DateTime.UtcNow > end) throw new TimeoutException("wait"); await Task.Delay(150); }
    }

    // ---------- the script ----------
    public static async Task RunAsync(MainWindow main, string outDir)
    {
        _out = outDir; Directory.CreateDirectory(_out);
        try
        {
            Place(main); await Task.Delay(2500);
            var list = (ListBox)main.FindName("MessageList");
            await Until(() => list.Items.Count >= 8);
            MailHeader Msg(uint uid) => list.Items.OfType<MailHeader>().First(m => m.Uid == uid);
            ListBoxItem? Row(uint uid) => list.ItemContainerGenerator.ContainerFromItem(Msg(uid)) as ListBoxItem;
            list.SelectedItem = Msg(7); await Idle(1200);

            async Task Select(uint uid, int pause)
            {
                if (Row(uid) is { } row) await Move(Center(main, row, 0.45, 0.5), 600);
                var fx = ClickFx(); await Task.Delay(90); list.SelectedItem = Msg(uid); await fx; await Idle(pause);
            }

            async Task<Window> OpenChip(string file, Func<Window, bool>? ready = null)
            {
                var chip = FindEl<ButtonBase>(main, b => b.Tag is AttachmentInfo a && a.FileName == file);
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(chip, 650);
                await Task.Delay(400);
                Rec.Stop();
                var end = DateTime.UtcNow.AddSeconds(120); Window? found = null; int stable = 0;
                while (DateTime.UtcNow < end)
                {
                    await Task.Delay(700);
                    var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsVisible && !before.Contains(x));
                    if (w != null && w == found && (ready?.Invoke(w) ?? true)) { if (++stable >= 2) break; } else stable = 0;
                    found = w;
                }
                if (found == null) throw new TimeoutException(file);
                Place(found); await Idle(1500);
                return found;
            }

            // 1) inbox: click through mails
            Rec.Start(main, "c01-inbox");
            await Task.Delay(700);
            await Select(1, 1300); await Select(2, 1100); await Select(3, 1000); await Select(6, 1000); await Select(1, 900);

            // 2) AI summary (offline demo AI) + reply draft into the inline composer
            Rec.Start(main, "c02-ai");
            await Task.Delay(300);
            await Click(Btn(main, L.T("AI Özet")));
            await Task.Delay(5200);
            var draft = Btn(main, L.T("Cevap taslağı"));
            await Click(draft);
            await Task.Delay(5000);
            var use = Visuals(main).OfType<ButtonBase>().LastOrDefault(b => b.IsVisible && TextOf(b).Contains(ChatMessage.UseAsReplyLabel));
            await Click(use);
            await Task.Delay(3500);
            Rec.Stop();
            // discard the demo draft without prompts
            if (main.GetType().GetField("_inlineCompose", Any)?.GetValue(main) is { } ic) Call(ic, "CloseForTransfer");
            await Idle(800);
        }
        catch (Exception ex) { Log.Add("FAIL A " + ex); Finish(); return; }

        try
        {
            var list = (ListBox)main.FindName("MessageList");
            MailHeader Msg(uint uid) => list.Items.OfType<MailHeader>().First(m => m.Uid == uid);
            async Task<Window> OpenChip(uint uid, string file, string scene, Func<Window, bool>? ready = null)
            {
                Rec.Start(main, scene + "-a");
                if (list.SelectedItem != Msg(uid)) { list.SelectedItem = Msg(uid); await Idle(1200); }
                var chip = FindEl<ButtonBase>(main, b => b.Tag is AttachmentInfo a && a.FileName == file);
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(chip, 650);
                await Task.Delay(300);
                Rec.Stop();
                var end = DateTime.UtcNow.AddSeconds(120); Window? found = null; int stable = 0;
                while (DateTime.UtcNow < end)
                {
                    await Task.Delay(700);
                    var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsVisible && !before.Contains(x));
                    if (w != null && w == found && (ready?.Invoke(w) ?? true)) { if (++stable >= 3) break; } else stable = 0;
                    found = w;
                }
                if (found == null) throw new TimeoutException(file);
                Place(found); await Idle(2500);
                Rec.Cursor = new Point(W * 0.55, H * 0.5);
                return found;
            }

            // 3) PDF studio
            var pdf = await OpenChip(1, "flange-DN100.pdf", "c03-pdf"); await Idle(2500);
            Rec.Start(pdf, "c03-pdf");
            await Move(new Point(W * 0.45, H * 0.45), 500);
            for (int i = 0; i < 14; i++) { Call(pdf, "SetZoom", 0.6 + i * 0.075, null); await Task.Delay(140); }
            await Move(new Point(W * 0.62, H * 0.55), 700);
            await Task.Delay(800);
            for (int i = 0; i < 8; i++) { Call(pdf, "SetZoom", 1.6 - i * 0.12, null); await Task.Delay(140); }
            await Task.Delay(500);
            Rec.Stop(); pdf.Close(); await Idle(500);

            // 4) DXF: zoom, switch a layer off and on
            var cad = await OpenChip(1, "flange-DN100.dxf", "c04-dxf"); await Idle(3500);
            Rec.Start(cad, "c04-dxf");
            var canvas = cad.GetType().GetField("_canvas", Any)!.GetValue(cad)!;
            var dims = FindEl<CheckBox>(cad, c => TextOf(c).Contains("HATCH"));
            await Click(dims, 700); await Task.Delay(700); await Click(dims, 300); await Task.Delay(500);
            await Move(new Point(W * 0.42, H * 0.5), 600);
            for (int i = 0; i < 10; i++) { Call(canvas, "ZoomBy", 1.12); await Task.Delay(110); }
            await Task.Delay(600);
            for (int i = 0; i < 10; i++) { Call(canvas, "ZoomBy", 1 / 1.12); await Task.Delay(90); }
            await Task.Delay(400);
            Rec.Stop(); cad.Close(); await Idle(500);

            // 5) STEP: orbit
            list.SelectedItem = Msg(2); await Idle(1200);
            var step = await OpenChip(2, "bracket-revC.stp", "c05-step", w => w.GetType().GetField("_loaded", Any)?.GetValue(w) is true);
            await Idle(2500);
            Rec.Start(step, "c05-step");
            await Move(new Point(W * 0.5, H * 0.5), 500);
            for (int i = 0; i < 60; i++) { Call(step, "Orbit", 6.0, i < 30 ? 0.6 : -0.6); await Task.Delay(55); }
            await Task.Delay(400);
            Rec.Stop(); step.Close(); await Idle(500);

            // 6) Office without Office: Excel, Word, PowerPoint
            var xlsx = await OpenChip(3, "delivery-schedule-wk43.xlsx", "c06-excel"); await Idle(2500);
            Rec.Start(xlsx, "c06-excel");
            await Move(new Point(W * 0.47, H * 0.215), 700); var fx = ClickFx(); await fx;
            await Move(new Point(W * 0.36, H * 0.215), 700);
            await Task.Delay(1400);
            Rec.Stop(); xlsx.Close(); await Idle(500);

            Window Newest() => Application.Current.Windows.OfType<Window>().Last(x => x.IsVisible && x != main);
            var docx = await OpenChip(4, "FAI-report-NW-1042.docx", "c07-word"); await Idle(8000);
            docx = Newest(); Place(docx); await Idle(2500);
            Rec.Start(docx, "c07-word"); await Move(new Point(W * 0.5, H * 0.4), 900); await Task.Delay(1800); Rec.Stop(); docx.Close(); await Idle(500);

            var pptx = await OpenChip(2, "kickoff-bracket-revC.pptx", "c08-ppt"); await Idle(8000);
            pptx = Newest(); Place(pptx); await Idle(2000);
            Rec.Start(pptx, "c08-ppt"); await Move(new Point(W * 0.12, H * 0.45), 900); await Task.Delay(1800); Rec.Stop(); pptx.Close(); await Idle(500);

            var nc = await OpenChip(5, "OP20.nc", "c09-cnc"); await Idle(1500);
            Rec.Start(nc, "c09-cnc"); await Move(new Point(W * 0.35, H * 0.4), 900); await Task.Delay(1600); Rec.Stop(); nc.Close(); await Idle(500);

            // 7) conditional formatting, dark theme
            var before = Application.Current.Windows.OfType<Window>().ToHashSet();
            _ = main.Dispatcher.BeginInvoke(() => Call(main, "OnFormatRules", main, new RoutedEventArgs()));
            var rules = await WaitNew<Views.FormatRulesWindow>(before); await Idle(1500);
            Rec.Start(rules, "c10-rules"); Rec.Cursor = new Point(rules.ActualWidth * 0.5, rules.ActualHeight * 0.5);
            if (FindEl<ListBox>(rules, _ => true) is { } rl && rl.Items.Count > 0)
            {
                for (int i = 0; i < Math.Min(2, rl.Items.Count); i++)
                {
                    if (rl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement it) await Move(it, 600);
                    var cf = ClickFx(); await Task.Delay(90); rl.SelectedIndex = i; await cf; await Task.Delay(900);
                }
            }
            else await Move(new Point(rules.ActualWidth * 0.3, rules.ActualHeight * 0.3), 800);
            await Task.Delay(800); Rec.Stop(); rules.Close(); await Idle(800);

            list.SelectedItem = Msg(1); await Idle(1200);
            Rec.Start(main, "c11-dark"); Rec.Cursor = new Point(W * 0.5, H * 0.5);
            await Task.Delay(600);
            ((RadioButton)main.FindName("ThemeDark")).IsChecked = true; await Idle(400);
            await Task.Delay(500);
            list.SelectedItem = Msg(2); await Idle(1500);
            await Task.Delay(800);
            ((RadioButton)main.FindName("ThemeLight")).IsChecked = true; await Idle(400);
            await Task.Delay(700);
            Rec.Stop();
            Log.Add("OK");
        }
        catch (Exception ex) { Log.Add("FAIL B " + ex); }
        Finish();
    }

    /// <summary>`--showcase-skills &lt;folder&gt;`: the 1.6 film – skills on mail + attachments, Excel preview, writing a
    /// skill with AI, one-click agent connection (agent config files are redirected to the output folder).</summary>
    public static async Task RunSkillsAsync(MainWindow main, string outDir)
    {
        _out = outDir; Directory.CreateDirectory(_out);
        AgentConnector.TestRoot = Path.Combine(_out, "agent-configs");
        try
        {
            Place(main); await Task.Delay(2500);
            var list = (ListBox)main.FindName("MessageList");
            await Until(() => list.Items.Count >= 8);
            MailHeader Msg(uint uid) => list.Items.OfType<MailHeader>().First(m => m.Uid == uid);
            var send = (ButtonBase)main.FindName("AiSendButton");
            ButtonBase? SkillBtn(string id) => FindEl<ButtonBase>(main, b => TextOf(b).StartsWith("⚡") && TextOf(b).Contains(new Services.Skills.SkillStore().All().First(k => k.Id == id).Title));
            async Task Select(uint uid, int pause)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(Msg(uid)) is ListBoxItem row) await Move(Center(main, row, 0.45, 0.5), 600);
                var fx = ClickFx(); await Task.Delay(90); list.SelectedItem = Msg(uid); await fx; await Idle(pause);
            }
            list.SelectedItem = Msg(7); await Idle(1500);

            // 1) quotation request: the skill reads the mail, the PDF (OCR) and the DXF
            Rec.Start(main, "c21-skill");
            await Task.Delay(500);
            await Select(1, 1400);
            await Click(SkillBtn("quote-request-analysis"), 800);
            await Task.Delay(1500);
            await Until(() => send.IsEnabled, 90);
            await Move(new Point(W * 0.86, H * 0.62), 900);
            await Task.Delay(2600);
            Rec.Stop();

            // 2) Excel skill: plan -> preview window (nothing written)
            Rec.Start(main, "c22-excel-a");
            await Select(3, 1200);
            var before = Application.Current.Windows.OfType<Window>().ToHashSet();
            await Click(SkillBtn("delivery-schedule-update"), 800);
            var plan = await WaitNew<Views.ExcelPlanWindow>(before, null, 90);
            Rec.Stop();
            plan.Left = 40; plan.Top = 40; plan.Width = 1180; plan.Height = 664; await Idle(1200);
            Rec.Start(plan, "c22-excel"); Rec.Cursor = new Point(600, 420);
            var grid = FindEl<DataGrid>(plan, _ => true);
            if (grid != null && grid.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement r0) { await Move(Center(plan, r0, 0.75, 0.5), 800); await Task.Delay(900); }
            if (grid != null && grid.ItemContainerGenerator.ContainerFromIndex(2) is FrameworkElement r2) { await Move(Center(plan, r2, 0.75, 0.5), 700); await Task.Delay(700); }
            var save = FindEl<Button>(plan, b => b.IsDefault);
            if (save != null) { await Move(save, 800); await Task.Delay(1400); }
            Rec.Stop(); plan.Close(); await Idle(1200);

            // 3) Skills window: built-in skills, describe a new one, the AI writes it, save
            before = Application.Current.Windows.OfType<Window>().ToHashSet();
            _ = main.Dispatcher.BeginInvoke(() => Call(main, "OnSkills", main, new RoutedEventArgs()));
            var sw = await WaitNew<Views.SkillsWindow>(before); Place(sw); await Idle(1500);
            Rec.Start(sw, "c23-skills"); Rec.Cursor = new Point(W * 0.5, H * 0.5);
            var lb = Field<ListBox>(sw, "_list");
            foreach (var i in new[] { 1, 4 })
            {
                if (i < lb.Items.Count && lb.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement it) await Move(it, 650);
                var cf = ClickFx(); await Task.Delay(90); if (i < lb.Items.Count) lb.SelectedIndex = i; await cf; await Task.Delay(1100);
            }
            var box = Field<TextBox>(sw, "_aiBox");
            await Move(box, 700); var bf = ClickFx(); await Task.Delay(90); box.Focus(); await bf;
            var want = L.IsEnglish ? "When a supplier reports a delay, list the affected orders with old and new dates and the questions to ask."
                                   : "Tedarikçi gecikme bildirince etkilenen siparişleri eski ve yeni tarihleriyle listele, sorulacak soruları yaz.";
            for (int i = 1; i <= want.Length; i += 2) { box.Text = want[..Math.Min(i, want.Length)]; box.CaretIndex = box.Text.Length; await Task.Delay(28); }
            box.Text = want; await Task.Delay(500);
            await Click(Field<Button>(sw, "_aiButton"), 700);
            await Until(() => Field<Button>(sw, "_aiButton").IsEnabled, 60);
            await Task.Delay(800);
            await Move(Field<TextBox>(sw, "_editor"), 900); await Task.Delay(1200);
            await Click(Field<Button>(sw, "_save"), 800);
            await Task.Delay(1800);
            Rec.Stop(); sw.Close(); await Idle(1000);

            // 4) Connect agents: one click, choose the permission, test the server
            before = Application.Current.Windows.OfType<Window>().ToHashSet();
            _ = main.Dispatcher.BeginInvoke(() => Call(main, "OnAgents", main, new RoutedEventArgs()));
            var ag = await WaitNew<Views.AgentsWindow>(before); Place(ag); await Idle(1500);
            Rec.Start(ag, "c24-agents"); Rec.Cursor = new Point(W * 0.5, H * 0.45);
            var rows = Field<StackPanel>(ag, "_rows");
            // cards are found by their config file (shown as plain text): .claude.json, claude_desktop_config.json, config.toml
            FrameworkElement? Card(string file) => rows.Children.OfType<FrameworkElement>().FirstOrDefault(c => Visuals(c).OfType<TextBlock>().Any(t => t.Text.EndsWith(file, StringComparison.OrdinalIgnoreCase)));
            ButtonBase? CardBtn(string name, string text) => Card(name) is { } c ? Visuals(c).OfType<ButtonBase>().FirstOrDefault(b => TextOf(b) == text) : null;
            await Task.Delay(700);
            await Click(CardBtn("claude_desktop_config.json", L.T("Bağla")), 900); await Task.Delay(1500);
            await Click(CardBtn("\\.claude.json", L.T("Bağla")), 800); await Task.Delay(1300);
            var combo = Card("config.toml") is { } cc ? Visuals(cc).OfType<ComboBox>().FirstOrDefault() : null;
            if (combo != null) { await Move(combo, 800); var c2 = ClickFx(); await Task.Delay(90); combo.SelectedIndex = 0; await c2; await Task.Delay(700); }
            await Click(CardBtn("config.toml", L.T("Bağla")), 700); await Task.Delay(1300);
            await Click(CardBtn("\\.claude.json", L.T("Test et")), 800);
            var status = Field<TextBlock>(ag, "_status");
            try { await Until(() => status.Text.StartsWith("✓") || status.Text.StartsWith("✗"), 40); } catch { }
            await Move(status, 900);
            await Task.Delay(2600);
            Rec.Stop(); ag.Close(); await Idle(800);
            Log.Add("OK");
        }
        catch (Exception ex) { Log.Add("FAIL S " + ex); }
        Finish();
    }

    static void Finish()
    {
        try { Rec.Stop(); } catch { }
        File.WriteAllLines(Path.Combine(_out, "showcase.log"), Log);
        Environment.Exit(0);
    }

    static void ForceClose(Views.ComposeWindow c)
    {
        try { typeof(Views.ComposeWindow).GetField("_confirmClose", Any)?.SetValue(c, (Func<MessageBoxResult>)(() => MessageBoxResult.No)); } catch { }
        try { c.Close(); } catch { }
    }

    /// <summary>
    /// `--showcase-18 &lt;folder&gt;`: the 1.8 scenes, each one really doing the thing it shows (pick the earlier revision,
    /// compare, add the changes to the reply, undo a send, snooze, follow-up...). Every scene is at least ~5 s.
    /// The screen must stay unlocked while recording (a locked session gives empty frames).
    /// </summary>
    public static async Task Run18Async(MainWindow main, string outDir)
    {
        _out = outDir; Directory.CreateDirectory(_out);
        AppSettings.Shared.UndoSendSeconds = 10;
        try
        {
            Place(main); await Task.Delay(2500);
            var list = (ListBox)main.FindName("MessageList");
            await Until(() => list.Items.Count >= 8);
            var cache = Field<MailCache>(main, "_cache");
            var all = cache.GetHeaders(DemoData.AccountId, "INBOX", 200);
            MailHeader With(string file) => all.First(h => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).Any(a => a.FileName == file));
            AttachmentInfo Att(MailHeader h, string file) => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).First(a => a.FileName == file);
            async Task<string> Download(MailHeader h, string file) => (await (Task<string?>)Call(main, "DownloadAttachmentAsync", h, Att(h, file))!)!;
            ListBoxItem? Row(MailHeader m) => list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == m.Uid)) as ListBoxItem;
            async Task Select(MailHeader m, int pause = 900)
            {
                if (Row(m) is { } row) await Move(Center(main, row, 0.45, 0.5), 600);
                var fx = ClickFx(); await Task.Delay(90);
                list.SelectedItem = list.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == m.Uid); await fx; await Idle(pause);
            }

            // ---- c31: revision comparison: the earlier revision is found in Anna's older mail, compared, added to the reply
            try
            {
                var revC = With("flange-DN100-revC.dxf");
                list.SelectedItem = list.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == revC.Uid); await Idle(1500);
                Rec.Start(main, "c31-mail");
                Rec.Cursor = new Point(W * 0.3, H * 0.4);
                await Select(revC, 1200);
                var chip = FindEl<ButtonBase>(main, b => b.Tag is AttachmentInfo a && a.FileName == "flange-DN100-revC.dxf");
                if (chip != null) { await Move(chip, 700); var fx = ClickFx(); await Task.Delay(400); await fx; }
                await Task.Delay(900);
                Rec.Stop();

                var newPath = await Download(revC, "flange-DN100-revC.dxf");
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                var pick = main.Dispatcher.InvokeAsync(() => (Task<string?>)Call(main, "PickOlderRevisionAsync", main, newPath)!);
                var chooser = await WaitNew<Window>(before, w => w.Title == L.T("Önceki revizyonu seçin"), 20);
                await Idle(700);
                Rec.Start(chooser, "c31a-choose");
                Rec.Cursor = new Point(chooser.ActualWidth * 0.3, chooser.ActualHeight * 0.3);
                var lb = FindEl<ListBox>(chooser, _ => true);
                if (lb != null) { await Move(Center(chooser, lb, 0.3, 0.08), 800); await Task.Delay(1200); }
                var cmp = Btn(chooser, L.T("Karşılaştır"));
                await Move(cmp!, 700); await Task.Delay(500);
                var cfx = ClickFx(); await Task.Delay(120); Rec.Stop();
                cmp!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cmp)); await cfx;
                var oldPath = await await pick.Task;

                before = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = (Task)Call(main, "ShowRevisionDiffAsync", oldPath!, newPath, revC)!;
                var diff = await WaitNew<Views.RevisionDiffWindow>(before, null, 90);
                Place(diff); await Idle(1500);
                Rec.Start(diff, "c31-revdiff");
                Rec.Cursor = new Point(W * 0.5, H * 0.75);
                var lv = FindEl<ListView>(diff, _ => true);
                if (lv != null)
                {
                    for (int i = 0; i < Math.Min(5, lv.Items.Count); i++)
                    {
                        if (lv.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem it) { await Move(Center(diff, it, 0.35, 0.5), 500); lv.SelectedIndex = i; await Task.Delay(700); }
                    }
                }
                await Click(Btn(diff, L.T("Farkı Yanıta Ekle")), 700);
                await Task.Delay(800);
                Rec.Stop();
                diff.Close(); await Idle(2500);
                Rec.Start(main, "c31b-reply");
                Rec.Cursor = new Point(W * 0.62, H * 0.55);
                await Move(new Point(W * 0.6, H * 0.42), 900); await Task.Delay(2600);
                Rec.Stop();
                foreach (var c in main.OwnedWindows.OfType<Views.ComposeWindow>().ToList()) ForceClose(c);
                var inline = Field<Views.ComposeWindow?>(main, "_inlineCompose"); if (inline != null) ForceClose(inline);
                await Idle(800);
            }
            catch (Exception ex) { Log.Add("FAIL revision: " + ex); }

            // ---- c32: STEP revision: holes by diameter, volume, size
            try
            {
                var stepMail = With("bracket-revB.stp");
                var o = await Download(stepMail, "bracket-revB.stp"); var n = await Download(stepMail, "bracket-revC.stp");
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = (Task)Call(main, "ShowRevisionDiffAsync", o, n, stepMail)!;
                var diff = await WaitNew<Views.RevisionDiffWindow>(before, null, 120);
                Place(diff); await Idle(1500);
                Rec.Start(diff, "c32-step");
                Rec.Cursor = new Point(W * 0.5, H * 0.7);
                var lv = FindEl<ListView>(diff, _ => true);
                if (lv != null)
                    for (int i = 0; i < Math.Min(4, lv.Items.Count); i++)
                        if (lv.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem it) { await Move(Center(diff, it, 0.3, 0.5), 500); lv.SelectedIndex = i; await Task.Delay(800); }
                await Task.Delay(1200);
                Rec.Stop();
                diff.Close(); await Idle(800);
            }
            catch (Exception ex) { Log.Add("FAIL step: " + ex); }

            // ---- c33: quote desk: search, select, mark as won
            try
            {
                main.SwitchModule(PimModule.Quotes); await Idle(1500);
                var desk = Field<Views.Quotes.QuoteDeskView?>(main, "_quoteDeskView")!;
                Rec.Start(main, "c33-quotes");
                Rec.Cursor = new Point(W * 0.5, H * 0.55);
                var search = FindEl<TextBox>(main, t => t.IsVisible && t.ActualWidth > 100);
                if (search != null) await Move(search, 700);
                foreach (var ch in "NW-") { desk.SearchText += ch; await Task.Delay(220); }
                await Task.Delay(900);
                desk.SearchText = ""; await Task.Delay(500);
                var first = Field<ListView>(desk, "_list");
                if (first.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem row) { await Move(Center(main, row, 0.3, 0.5), 600); first.SelectedIndex = 0; await Task.Delay(700); }
                await Click(Btn(main, L.T("Kazanıldı")), 700);
                await Task.Delay(1400);
                Rec.Stop();
            }
            catch (Exception ex) { Log.Add("FAIL quotes: " + ex); }

            // ---- c34: rules: test a rule on the sample, show the result
            try
            {
                main.SwitchModule(PimModule.Mail); await Idle(800);
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = main.Dispatcher.BeginInvoke(() => Call(main, "OnRules", main, new RoutedEventArgs()));
                var rw = await WaitNew<Views.RulesWindow>(before, null, 20);
                Place(rw); await Idle(1200);
                Rec.Start(rw, "c34-rules");
                Rec.Cursor = new Point(W * 0.2, H * 0.3);
                var lbx = FindEl<ListBox>(rw, _ => true);
                if (lbx?.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem r0) { await Move(Center(rw, r0, 0.4, 0.5), 600); await Task.Delay(800); }
                await Click(Btn(rw, L.T("Bu kuralı test et")), 800);
                await Task.Delay(2200);
                Rec.Stop();
                rw.Close(); await Idle(800);
            }
            catch (Exception ex) { Log.Add("FAIL rules: " + ex); }

            // ---- c35: missing attachment warning, then send → undo
            try
            {
                var anna = all.First(h => h.FromAddress.StartsWith("anna", StringComparison.OrdinalIgnoreCase));
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                Call(main, "OpenCompose", new ComposeRequest { To = anna.FromAddress, Subject = L.T("NW-1042 rev C – teklifimiz"), Body = L.T("Merhaba Anna, teklifimiz ektedir.") }, DemoData.AccountId, true);
                var comp = await WaitNew<Views.ComposeWindow>(before, null, 20);
                Place(comp); await Until(() => comp.IsEditorInitialized, 30); await Idle(1500);
                Rec.Start(comp, "c35a-compose");
                Rec.Cursor = new Point(W * 0.4, H * 0.5);
                await Move(new Point(W * 0.35, H * 0.42), 700); await Task.Delay(900);
                var send = Btn(comp, L.T("Gönder"));
                before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(send, 700);
                await Task.Delay(500);
                Rec.Stop();
                var dlg = await WaitNew<Window>(before, w => w.Title == L.T("Ek unutulmuş olabilir"), 15);
                await Idle(600);
                Rec.Start(dlg, "c35b-missing");
                Rec.Cursor = new Point(dlg.ActualWidth * 0.5, dlg.ActualHeight * 0.4);
                await Task.Delay(1800);
                var cancel = Btn(dlg, L.T("Dosya Ekle")) ?? Btn(dlg, L.T("İptal"));
                if (cancel != null) await Move(cancel, 700);
                await Task.Delay(800);
                Rec.Stop();
                dlg.Close(); await Idle(800);
                // a mail without "attached": Send → the undo bar
                ForceClose(comp); await Idle(600);
                before = Application.Current.Windows.OfType<Window>().ToHashSet();
                Call(main, "OpenCompose", new ComposeRequest { To = anna.FromAddress, Subject = L.T("NW-1042 rev C onayı"), Body = L.T("Merhaba Anna, rev C çizimi onaylandı.") }, DemoData.AccountId, true);
                comp = await WaitNew<Views.ComposeWindow>(before, null, 20);
                Place(comp); await Until(() => comp.IsEditorInitialized, 30); await Idle(1200);
                Btn(comp, L.T("Gönder"))!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, Btn(comp, L.T("Gönder"))));
                await Until(() => ((FrameworkElement)main.FindName("UndoSendBar")).IsVisible, 15);
                await Idle(500);
                Rec.Start(main, "c35-undo");
                Rec.Cursor = new Point(W * 0.5, H * 0.6);
                await Task.Delay(2200);
                before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click((ButtonBase)main.FindName("UndoSendButton"), 900);
                await Task.Delay(1600);
                Rec.Stop();
                foreach (var c in Application.Current.Windows.OfType<Views.ComposeWindow>().Where(c => !before.Contains(c)).ToList()) ForceClose(c);
            }
            catch (Exception ex) { Log.Add("FAIL undo: " + ex); }

            // ---- c36: send later → Outbox
            try
            {
                var acc = Field<AccountStore>(main, "_store").Accounts.First();
                main.ScheduleSend(acc, new ComposeRequest { To = "anna.keller@northwind-machining.example", Subject = L.T("Haftalık teslim planı"), Body = "x" }, DateTimeOffset.Now.Date.AddDays(1).AddHours(8));
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = main.Dispatcher.BeginInvoke(() => Call(main, "ShowOutboxWindow"));
                var ow = await WaitNew<Views.OutboxWindow>(before, null, 15);
                await Idle(900);
                Rec.Start(ow, "c36-outbox");
                Rec.Cursor = new Point(ow.ActualWidth * 0.5, ow.ActualHeight * 0.5);
                await Move(new Point(ow.ActualWidth * 0.3, ow.ActualHeight * 0.22), 700); await Task.Delay(1500);
                var edit = Btn(ow, L.T("Düzenle")); if (edit != null) await Move(edit, 700);
                await Task.Delay(1500);
                Rec.Stop();
                ow.Close(); await Idle(600);
            }
            catch (Exception ex) { Log.Add("FAIL outbox: " + ex); }

            // ---- c37: follow-up task and the reminder draft
            try
            {
                main.SwitchModule(PimModule.Tasks); await Idle(1500);
                Rec.Start(main, "c37-followup");
                Rec.Cursor = new Point(W * 0.4, H * 0.3);
                var item = FindEl<ListBoxItem>(main, i => i.Tag is Services.Calendar.TaskItem t && t.Category == "Cevap bekleniyor");
                if (item != null) { await Move(Center(main, item, 0.3, 0.5), 700); item.IsSelected = true; await Task.Delay(1200); }
                var remind = Btn(main, L.T("Hatırlatma maili yaz"));
                if (remind != null) await Move(remind, 800);
                await Task.Delay(1600);
                Rec.Stop();
            }
            catch (Exception ex) { Log.Add("FAIL followup: " + ex); }

            // ---- c38: snooze → back later; Snoozed tab
            try
            {
                main.SwitchModule(PimModule.Mail); await Idle(1000);
                var m = list.Items.OfType<MailHeader>().Skip(2).First();
                Rec.Start(main, "c38-snooze");
                Rec.Cursor = new Point(W * 0.4, H * 0.5);
                await Select(m, 800);
                var snoozeBtn = Btn(main, L.T("Ertele"));
                if (snoozeBtn != null) { await Move(snoozeBtn, 700); var fx = ClickFx(); await Task.Delay(200); await fx; }
                main.SnoozeSelected(DateTimeOffset.Now.Date.AddDays(1).AddHours(9));
                await Task.Delay(1400);
                await Click((ButtonBase)main.FindName("FilterSnoozed"), 800);
                await Task.Delay(1600);
                await Click((ButtonBase)main.FindName("FilterAll"), 700);
                await Task.Delay(600);
                Rec.Stop();
            }
            catch (Exception ex) { Log.Add("FAIL snooze: " + ex); }

            // ---- c39: all inboxes, search folder, newsletter unsubscribe bar
            try
            {
                var roots = Field<System.Collections.ObjectModel.ObservableCollection<FolderNode>>(main, "_roots");
                Rec.Start(main, "c39-inbox");
                Rec.Cursor = new Point(W * 0.08, H * 0.25);
                var unified = roots.FirstOrDefault(r => r.IsUnifiedInbox);
                if (unified != null) { await Move(new Point(W * 0.07, H * 0.215), 600); Call(main, "SelectFolder", unified); await Task.Delay(1300); }
                var sf = roots.FirstOrDefault(r => r.IsSearchFolderRoot)?.Children.LastOrDefault();
                if (sf != null) { await Move(new Point(W * 0.08, H * 0.51), 700); Call(main, "SelectFolder", sf); await Task.Delay(1500); }
                if (unified != null) { Call(main, "SelectFolder", unified); await Idle(800); }
                var news = list.Items.OfType<MailHeader>().FirstOrDefault(x => NewsletterHelper.IsNewsletter(x));
                if (news != null) await Select(news, 1500);
                var unsub = main.FindName("UnsubscribeButton") as FrameworkElement;
                if (unsub is { IsVisible: true }) await Move(unsub, 800);
                await Task.Delay(1800);
                Rec.Stop();
            }
            catch (Exception ex) { Log.Add("FAIL inbox: " + ex); }

            // ---- c40: calendar week and the account calendar choice in the event form
            try
            {
                main.SwitchModule(PimModule.Calendar); await Idle(1200);
                var cv = Field<CalendarView?>(main, "_calendarView");
                cv?.SetMode(CalendarViewMode.Week); await Idle(800);
                Rec.Start(main, "c40-calendar");
                Rec.Cursor = new Point(W * 0.5, H * 0.5);
                await Move(new Point(W * 0.55, H * 0.45), 700); await Task.Delay(1000);
                var sync = Btn(main, L.T("Şimdi Eşitle"));
                if (sync != null) await Move(sync, 800);
                await Task.Delay(1500);
                Rec.Stop();
                var acc = Field<AccountStore>(main, "_store").Accounts.First();
                acc.EnableCalendarSync = true;
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = main.Dispatcher.BeginInvoke(() => cv!.NewEvent(false));
                var ew = await WaitNew<EventWindow>(before, null, 15);
                await Idle(900);
                Rec.Start(ew, "c40b-event");
                Rec.Cursor = new Point(ew.ActualWidth * 0.4, ew.ActualHeight * 0.3);
                var combo = FindEl<ComboBox>(ew, c => c.Items.OfType<ComboBoxItem>().Any(i => (i.Tag as string) == acc.Id));
                if (combo != null) { await Move(combo, 900); combo.SelectedIndex = 1; await Task.Delay(1500); }
                await Task.Delay(800);
                Rec.Stop();
                ew.Close(); acc.EnableCalendarSync = false;
                main.SwitchModule(PimModule.Mail);
            }
            catch (Exception ex) { Log.Add("FAIL calendar: " + ex); }

            Log.Add("OK 18");
        }
        catch (Exception ex) { Log.Add("FAIL 18 " + ex); }
        Finish();
    }
    /// <summary>
    /// `--showcase-19 &lt;folder&gt;`: the 1.9 scenes (RAR, morning brief, cost estimate and quotation PDF, visual revision
    /// difference, part card, NC toolpath, search by meaning, PDF table to Excel, request-for-quote acknowledgement).
    /// Every scene really does what it shows on the demo mailbox.
    /// </summary>
    public static async Task Run19Async(MainWindow main, string outDir)
    {
        _out = outDir; Directory.CreateDirectory(_out);
        try
        {
            Place(main); await Task.Delay(2500);
            var list = (ListBox)main.FindName("MessageList");
            await Until(() => list.Items.Count >= 8);
            var cache = Field<MailCache>(main, "_cache");
            var all = cache.GetHeaders(DemoData.AccountId, "INBOX", 200);
            MailHeader With(string file) => all.First(h => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).Any(a => a.FileName == file));
            AttachmentInfo Att(MailHeader h, string file) => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).First(a => a.FileName == file);
            async Task<string> Download(MailHeader h, string file) => (await (Task<string?>)Call(main, "DownloadAttachmentAsync", h, Att(h, file))!)!;
            ListBoxItem? Row(MailHeader m) => list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == m.Uid)) as ListBoxItem;
            async Task Select(MailHeader m, int pause = 900)
            {
                if (Row(m) is { } row) await Move(Center(main, row, 0.45, 0.5), 600);
                var fx = ClickFx(); await Task.Delay(90);
                list.SelectedItem = list.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == m.Uid); await fx; await Idle(pause);
            }
            TechnicalWorker Worker() => (TechnicalWorker)Call(main, "SkillWorker")!;
            // demo company for the quotation
            var cs = Costing.CostEstimator.LoadSettings();
            if (cs.Company.Name.Length == 0)
            {
                cs.Company = new Costing.CompanyInfo { Name = "Northwind Machining (demo)", Address = "1 Demo Street\nDemo City", Email = "sales@northwind-machining.example", Phone = "+00 000 000 00 00",
                    Signature = L.IsEnglish ? "Best regards,\nSales · Northwind Machining (demo)" : "Saygılarımızla,\nSatış · Northwind Machining (demo)" };
                Costing.CostEstimator.SaveSettings(cs);
            }

            // ---- c50: RAR archive opens like a ZIP
            try
            {
                var rarMail = With("NW-1042-drawings.rar");
                Rec.Start(main, "c50-mail");
                Rec.Cursor = new Point(W * 0.3, H * 0.45);
                await Select(rarMail, 1000);
                var chip = FindEl<ButtonBase>(main, b => b.Tag is AttachmentInfo a && a.FileName == "NW-1042-drawings.rar");
                if (chip != null) { await Move(chip, 700); var fx = ClickFx(); await Task.Delay(300); await fx; }
                Rec.Stop();
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = (Task)Call(main, "OpenAttachmentAsync", rarMail, Att(rarMail, "NW-1042-drawings.rar"))!;
                var viewer = await WaitNew<Window>(before, null, 30);
                Place(viewer); await Idle(1800);
                Rec.Start(viewer, "c50-rar");
                Rec.Cursor = new Point(W * 0.3, H * 0.3);
                var rows = Visuals(viewer).OfType<ListViewItem>().Where(i => i.IsVisible).Take(4).ToList();
                foreach (var r in rows) { await Move(Center(viewer, r, 0.25, 0.5), 450); await Task.Delay(350); }
                await Task.Delay(1200);
                Rec.Stop(); viewer.Close(); await Idle(600);
            }
            catch (Exception ex) { Log.Add("FAIL rar: " + ex); }

            // ---- c51: morning brief
            try
            {
                var w = main.ShowMorningBrief(DateTime.Today, DateTimeOffset.Now.AddDays(-7));
                w.Width = 1100; w.Height = H; w.Left = 20; w.Top = 20; await Idle(1200);
                Rec.Start(w, "c51-brief");
                Rec.Cursor = new Point(w.ActualWidth * 0.5, w.ActualHeight * 0.2);
                var sv = FindEl<ScrollViewer>(w, _ => true);
                await Move(new Point(w.ActualWidth * 0.35, w.ActualHeight * 0.32), 800); await Task.Delay(900);
                if (sv != null) for (var y = 0; y < 14; y++) { sv.ScrollToVerticalOffset(y * 28); await Task.Delay(110); }
                await Move(new Point(w.ActualWidth * 0.4, w.ActualHeight * 0.55), 800); await Task.Delay(1400);
                Rec.Stop(); w.Close(); await Idle(500);
            }
            catch (Exception ex) { Log.Add("FAIL brief: " + ex); }

            // ---- c52: cost estimate from the STEP model, then the quotation PDF
            try
            {
                var stepMail = With("bracket-revC.stp");
                var path = await Download(stepMail, "bracket-revC.stp");
                var g = Costing.PartGeometry.FromExtract(await Worker().CallAsync("extract_step", new { path, ocr = false }));
                var quotes = Field<QuoteStore>(main, "_quoteStore");
                var q = (quotes.Quotes.FirstOrDefault(x => x.PartNumber.Contains("bracket", StringComparison.OrdinalIgnoreCase) && !x.AutoCreated) ?? quotes.Quotes.First()).Clone();
                var w = new Views.Quotes.CostEstimateWindow("bracket rev C – bracket-revC.stp", g, q) { Owner = main };
                w.Show(); w.Left = 20; w.Top = 20; w.Width = 900; w.Height = 760; await Idle(1200);
                Rec.Start(w, "c52-cost");
                Rec.Cursor = new Point(w.ActualWidth * 0.6, w.ActualHeight * 0.2);
                var combo = FindEl<ComboBox>(w, c => c.Items.Count > 10);
                if (combo != null)
                {
                    await Move(combo, 700); await Task.Delay(300);
                    var alu = combo.Items.OfType<Costing.MaterialSpec>().FirstOrDefault(m => m.Name.StartsWith("Al 6061"));
                    if (alu != null) { var fx = ClickFx(); combo.SelectedItem = alu; await fx; }
                    await Task.Delay(900);
                }
                var qty = FindEl<TextBox>(w, t => t.Text == q.Quantity.ToString(System.Globalization.CultureInfo.CurrentCulture));
                if (qty != null)
                {
                    await Move(qty, 600); qty.Text = "";
                    foreach (var ch in "500") { qty.Text += ch; await Task.Delay(220); }
                    await Task.Delay(900);
                }
                var unit = FindEl<TextBlock>(w, t => t.FontSize >= 20);
                if (unit != null) await Move(Center(w, unit, 0.4, 0.5), 700);
                await Task.Delay(1400);
                var apply = Btn(w, L.T("Teklife uygula"));
                if (apply != null) { await Move(apply, 700); await Task.Delay(500); }
                var est = Field<Costing.CostEstimate?>(w, "_estimate");
                Rec.Stop(); w.Close(); await Idle(400);
                if (est != null)
                {
                    q.UnitPrice = est.UnitPrice; q.Currency = est.Currency; q.Quantity = est.Quantity; q.Material = est.Material.Display;
                    q.QuoteNumber = Costing.QuoteDocument.NextNumber(quotes.Quotes, DateTime.Today);
                    var pdf = Path.Combine(_out, Costing.QuoteDocument.FileName(q));
                    await HtmlPdfPrinter.PrintAsync(Costing.QuoteDocument.BuildHtml(q, Costing.CostEstimator.LoadSettings(), DateTime.Today), pdf);
                    var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                    var pv = new Views.PdfAttachmentWindow(pdf, Views.FileViewers.DefaultAi()) { Owner = main };
                    pv.Show(); Place(pv); await Idle(3000);
                    Rec.Start(pv, "c52-quotepdf");
                    Rec.Cursor = new Point(W * 0.5, H * 0.35);
                    await Move(new Point(W * 0.55, H * 0.55), 1200); await Task.Delay(2200);
                    Rec.Stop(); pv.Close(); await Idle(500);
                }
            }
            catch (Exception ex) { Log.Add("FAIL cost: " + ex); }

            // ---- c53: visual revision difference (DXF overlay, then STEP faces)
            try
            {
                var oMail = With("flange-DN100-revB.dxf"); var nMail = With("flange-DN100-revC.dxf");
                var o = await Download(oMail, "flange-DN100-revB.dxf"); var n = await Download(nMail, "flange-DN100-revC.dxf");
                var vw = new Views.VisualDiffWindow(o, n, (args, ct) => Worker().CallAsync("visual_diff", args, ct), "flange-DN100 (B → C)") { Owner = main };
                vw.Show(); Place(vw); await vw.Ready; await Idle(1000);
                Rec.Start(vw, "c53-visual");
                Rec.Cursor = new Point(W * 0.4, H * 0.5);
                await Move(new Point(W * 0.3, H * 0.4), 900); await Task.Delay(900);
                var lb = FindEl<ListBox>(vw, l => l.Items.Count > 0);
                if (lb != null && lb.ItemContainerGenerator.ContainerFromIndex(1) is ListBoxItem item)
                {
                    await Move(Center(vw, item, 0.5, 0.5), 800); var fx = ClickFx(); lb.SelectedIndex = 1; await fx; await Task.Delay(1800);
                }
                await Click(Btn(vw, L.T("Sığdır")), 800); await Task.Delay(900);
                Rec.Stop(); vw.Close(); await Idle(500);

                var sMail = With("bracket-revB.stp");
                var so = await Download(sMail, "bracket-revB.stp"); var sn = await Download(sMail, "bracket-revC.stp");
                var sw = new Views.VisualDiffWindow(so, sn, (args, ct) => Worker().CallAsync("visual_diff", args, ct), "bracket (B → C)") { Owner = main };
                sw.Show(); Place(sw); await sw.Ready; await Idle(1000);
                Rec.Start(sw, "c53-step");
                Rec.Cursor = new Point(W * 0.85, H * 0.25);
                await Move(new Point(W * 0.27, H * 0.62), 1100); await Task.Delay(2200);
                Rec.Stop(); sw.Close(); await Idle(500);
            }
            catch (Exception ex) { Log.Add("FAIL visual: " + ex); }

            // ---- c54: part card
            try
            {
                var pc = main.OpenPartCard("NW-1042");
                Place(pc); await Idle(1200);
                Rec.Start(pc, "c54-partcard");
                Rec.Cursor = new Point(W * 0.4, H * 0.12);
                await Move(new Point(W * 0.3, H * 0.15), 700); await Task.Delay(800);
                var tabs = FindEl<TabControl>(pc, _ => true)!;
                foreach (var idx in new[] { 1, 3, 0 })
                {
                    if (tabs.ItemContainerGenerator.ContainerFromIndex(idx) is TabItem ti)
                    {
                        await Move(Center(pc, ti), 600); var fx = ClickFx(); tabs.SelectedIndex = idx; await fx; await Task.Delay(1400);
                    }
                }
                Rec.Stop(); pc.Close(); await Idle(500);
            }
            catch (Exception ex) { Log.Add("FAIL partcard: " + ex); }

            // ---- c55: NC toolpath: views, simulation, comparison
            try
            {
                var ncMail = With("OP20.nc");
                var nc = await Download(ncMail, "OP20.nc");
                var w = new Views.Nc.NcPreviewWindow(nc) { Owner = main };
                w.Show(); Place(w); await Idle(1200);
                Rec.Start(w, "c55-nc");
                Rec.Cursor = new Point(W * 0.55, H * 0.5);
                var slider = FindEl<Slider>(w, _ => true);
                if (slider != null)
                {
                    await Move(Center(w, slider, 0.0, 0.5), 700);
                    for (var i = 0; i <= 40; i++) { slider.Value = slider.Maximum * i / 40; Rec.Cursor = Center(w, slider, i / 40.0, 0.5); await Task.Delay(70); }
                }
                var top = FindEl<ToggleButton>(w, b => TextOf(b) == L.T("Üst (XY)"));
                if (top != null) { await Move(top, 600); var fx = ClickFx(); top.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, top)); await fx; await Task.Delay(1500); }
                var iso = FindEl<ToggleButton>(w, b => TextOf(b) == L.T("İzometrik"));
                if (iso != null) { await Move(iso, 500); var fx = ClickFx(); iso.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, iso)); await fx; await Task.Delay(900); }
                Rec.Stop(); w.Close(); await Idle(400);

                var changed = Path.Combine(_out, "OP20-revD.nc");
                File.WriteAllText(changed, File.ReadAllText(nc).Replace("F1500.", "F1000.").Replace("Z-14.", "Z-16."));
                var w2 = new Views.Nc.NcPreviewWindow(changed) { Owner = main };
                w2.Show(); Place(w2); await Idle(600);
                w2.CompareWith(nc); await Idle(1000);
                Rec.Start(w2, "c55-nccompare");
                Rec.Cursor = new Point(W * 0.15, H * 0.3);
                await Move(new Point(W * 0.14, H * 0.42), 800); await Task.Delay(1000);
                await Move(new Point(W * 0.88, H * 0.18), 900); await Task.Delay(1800);
                Rec.Stop(); w2.Close(); await Idle(400);
            }
            catch (Exception ex) { Log.Add("FAIL nc: " + ex); }

            // ---- c56: search by meaning
            try
            {
                AppSettings.Shared.SemanticModel = "";
                var w = main.OpenSemanticSearch();
                Place(w); await Idle(1000);
                await w.UpdateIndexAsync();
                Rec.Start(w, "c56-semantic");
                var box = FindEl<TextBox>(w, t => t.ActualWidth > 300)!;
                Rec.Cursor = new Point(W * 0.5, H * 0.3);
                await Move(Center(w, box, 0.2, 0.5), 700);
                var query = L.IsEnglish ? "deliveries planned for the coming weeks" : "önümüzdeki haftaların teslimatları";
                foreach (var ch in query) { box.Text += ch; await Task.Delay(55); }
                await Task.Delay(400);
                var go = Btn(w, L.T("Ara"));
                if (go != null) { await Move(go, 500); var fx = ClickFx(); await w.SearchAsync(); await fx; }
                await Move(new Point(W * 0.45, H * 0.36), 800); await Task.Delay(2200);
                Rec.Stop(); w.Close(); await Idle(400);
            }
            catch (Exception ex) { Log.Add("FAIL semantic: " + ex); }

            // ---- c57: PDF table → Excel
            try
            {
                var bomMail = With("NW-1042-parts-list.pdf");
                var pdf = await Download(bomMail, "NW-1042-parts-list.pdf");
                var w = new Views.PdfTablesWindow(pdf, (op, args, ct) => Worker().CallAsync(op, args, ct)) { Owner = main };
                w.Show(); Place(w); await w.Ready; await Idle(1000);
                Rec.Start(w, "c57-tables");
                Rec.Cursor = new Point(W * 0.1, H * 0.12);
                await Move(new Point(W * 0.12, H * 0.08), 600); await Task.Delay(600);
                await Move(new Point(W * 0.55, H * 0.3), 900); await Task.Delay(1400);
                var exp = Btn(w, L.T("Excel'e aktar…"));
                if (exp != null) { await Move(exp, 800); var fx = ClickFx(); await fx; }
                var xlsx = w.Export(Path.Combine(_out, "parts-list.xlsx"));
                await Task.Delay(1200);
                Rec.Stop(); w.Close(); await Idle(400);
                if (xlsx != null)
                {
                    var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                    var xv = Views.FileViewers.Create(xlsx, Views.FileViewers.DefaultAi());
                    if (xv != null)
                    {
                        xv.Show(); Place(xv); await Idle(3000);
                        Rec.Start(xv, "c57-excel");
                        Rec.Cursor = new Point(W * 0.3, H * 0.3);
                        await Move(new Point(W * 0.35, H * 0.4), 900); await Task.Delay(1800);
                        Rec.Stop(); xv.Close(); await Idle(400);
                    }
                }
            }
            catch (Exception ex) { Log.Add("FAIL tables: " + ex); }

            // ---- c58: request-for-quote intake and the acknowledgement draft
            try
            {
                var created = main.RfqIntakeFor(all);
                main.SwitchModule(PimModule.Quotes); await Idle(1200);
                var desk = Field<Views.Quotes.QuoteDeskView?>(main, "_quoteDeskView")!;
                var q = created.FirstOrDefault() ?? Field<QuoteStore>(main, "_quoteStore").Quotes.First(x => x.AutoCreated);
                desk.SelectQuote(q.Id); await Idle(600);
                Rec.Start(main, "c58-rfq");
                Rec.Cursor = new Point(W * 0.5, H * 0.6);
                var lv = Field<ListView>(desk, "_list");
                if (lv.SelectedItem != null && lv.ItemContainerGenerator.ContainerFromItem(lv.SelectedItem) is ListViewItem row) { await Move(Center(main, row, 0.3, 0.5), 800); await Task.Delay(900); }
                var ack = Btn(main, L.T("Alındı Yanıtı"));
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(ack, 800);
                await Task.Delay(700);
                Rec.Stop();
                var comp = Application.Current.Windows.OfType<Views.ComposeWindow>().FirstOrDefault(x => !before.Contains(x) && x.IsVisible)
                           ?? Field<Views.ComposeWindow?>(main, "_inlineCompose");
                if (comp != null)
                {
                    if (comp.IsVisible && !ReferenceEquals(comp, Field<Views.ComposeWindow?>(main, "_inlineCompose"))) { Place(comp); await Idle(4000); Rec.Start(comp, "c58-ack"); }
                    else { await Idle(2500); Rec.Start(main, "c58-ack"); }
                    Rec.Cursor = new Point(W * 0.5, H * 0.3);
                    await Move(new Point(W * 0.45, H * 0.5), 1000); await Task.Delay(2600);
                    Rec.Stop(); ForceClose(comp);
                }
                main.SwitchModule(PimModule.Mail);
            }
            catch (Exception ex) { Log.Add("FAIL rfq: " + ex); }

            // ---- c59: Outlook archive import (a demo archive source: no real .pst of the user is read)
            try
            {
                main.SwitchModule(PimModule.Mail); await Idle(800);
                Rec.Start(main, "c59-pst");
                Rec.Cursor = new Point(W * 0.5, H * 0.4);
                var tab = FindEl<RadioButton>(main, r => r.Tag as string == "folder");
                if (tab != null) { await Move(tab, 700); var fx = ClickFx(); tab.IsChecked = true; await fx; await Task.Delay(700); }
                var imp = Btn(main, "PST");
                if (imp != null) { await Move(imp, 700); var fx = ClickFx(); await fx; await Task.Delay(500); }
                var arch = Path.Combine(_out, L.IsEnglish ? "archive-2019.pst" : "arsiv-2019.pst");
                var result = await main.ImportArchiveAsync(new DemoArchive(), arch);
                await Idle(1400);
                if (result != null)
                {
                    var roots = Field<System.Collections.ObjectModel.ObservableCollection<FolderNode>>(main, "_roots");
                    var root = roots.FirstOrDefault(r => r.AccountId == result.Account.Id);
                    var inbox = root?.Children.FirstOrDefault(c => c.FullName.Contains("Inbox", StringComparison.OrdinalIgnoreCase) || c.FullName.Contains("Gelen", StringComparison.OrdinalIgnoreCase)) ?? root?.Children.FirstOrDefault();
                    if (inbox != null) { await Move(new Point(W * 0.08, H * 0.62), 800); Call(main, "SelectFolder", inbox); await Idle(1500); }
                    var first = list.Items.OfType<MailHeader>().FirstOrDefault(x => x.HasAttachments) ?? list.Items.OfType<MailHeader>().FirstOrDefault();
                    if (first != null) await Select(first, 2200);
                }
                var home = FindEl<RadioButton>(main, r => r.Tag as string == "home");
                if (home != null) home.IsChecked = true;
                await Task.Delay(600);
                Rec.Stop();
            }
            catch (Exception ex) { Log.Add("FAIL pst: " + ex); }

            Log.Add("OK 19");
        }
        catch (Exception ex) { Log.Add("FAIL 19 " + ex); }
        Finish();
    }
}

/// <summary>A small archive for the film (old demo mails); stands in for an Outlook .pst so no real archive is read.</summary>
sealed class DemoArchive : MailStudio.Services.Archive.IMailArchiveSource
{
    public IEnumerable<MailStudio.Services.Archive.ArchiveFolder> Folders()
    {
        var en = L.IsEnglish;
        MailStudio.Services.Archive.ArchiveMessage M(string subj, string from, string addr, int y, int mo, int d, string text, string? file = null) =>
            new(subj, from, addr, "demo@mailstudio.example", "", new DateTime(y, mo, d, 10, 30, 0, DateTimeKind.Utc), true, null, text, $"<demo-{y}{mo:00}{d:00}-{subj.GetHashCode():x}@archive.example>",
                file == null ? Array.Empty<MailStudio.Services.Archive.ArchiveAttachment>() :
                new[] { new MailStudio.Services.Archive.ArchiveAttachment(file, 64, s => { var b = System.Text.Encoding.UTF8.GetBytes("Pos;Part;Qty\n1;NW-0815;40\n2;NW-0816;40\n"); s.Write(b, 0, b.Length); }) });
        var inbox = new List<MailStudio.Services.Archive.ArchiveMessage>
        {
            M(en ? "Order NW-0815 – 40 pcs flange, delivery week 12" : "Sipariş NW-0815 – 40 adet flanş, teslim 12. hafta", "Anna Keller", "anna.keller@northwind-machining.example", 2019, 3, 4,
              en ? "Hello, please find our order for 40 flanges attached." : "Merhaba, 40 adet flanş siparişimiz ektedir.", "order-NW-0815.csv"),
            M(en ? "Drawing question – NW-0816 thread depth" : "Resim sorusu – NW-0816 diş derinliği", "Planning", "planning@northwind-machining.example", 2019, 2, 21,
              en ? "Is the M12 thread depth 18 mm or 20 mm?" : "M12 diş derinliği 18 mm mi 20 mm mi?"),
            M(en ? "Price list 2019" : "2019 fiyat listesi", "Lisa Wong", "lisa.wong@fabrikam-tools.example", 2019, 1, 14,
              en ? "Our tool price list for 2019 is attached." : "2019 takım fiyat listemiz ektedir.", en ? "price-list-2019.csv" : "fiyat-listesi-2019.csv"),
            M(en ? "Inspection report – NW-0815 first article" : "Muayene raporu – NW-0815 ilk numune", "Quality Lab", "quality@northwind-machining.example", 2019, 4, 2,
              en ? "First article accepted, see the notes." : "İlk numune kabul edildi, notlara bakın."),
            M(en ? "Holiday schedule" : "Tatil takvimi", "Tom Becker", "t.becker@contoso-aero.example", 2018, 12, 18,
              en ? "Our plant is closed between the holidays." : "Tesisimiz bayram arasında kapalıdır."),
        };
        var sent = new List<MailStudio.Services.Archive.ArchiveMessage>
        {
            M(en ? "Re: Order NW-0815 – confirmation" : "Ynt: Sipariş NW-0815 – onay", "Demo User", "demo@mailstudio.example", 2019, 3, 5,
              en ? "Thank you, delivery confirmed for week 12." : "Teşekkürler, 12. hafta teslim onaylandı."),
            M(en ? "Re: Drawing question – NW-0816" : "Ynt: Resim sorusu – NW-0816", "Demo User", "demo@mailstudio.example", 2019, 2, 22,
              en ? "18 mm full thread, as in rev B." : "Rev B'deki gibi 18 mm tam diş."),
        };
        return new[]
        {
            new MailStudio.Services.Archive.ArchiveFolder("/" + (en ? "Inbox" : "Gelen Kutusu"), en ? "Inbox" : "Gelen Kutusu", inbox.Count, () => inbox),
            new MailStudio.Services.Archive.ArchiveFolder("/" + (en ? "Sent Items" : "Gönderilmiş Öğeler"), en ? "Sent Items" : "Gönderilmiş Öğeler", sent.Count, () => sent),
        };
    }
    public void Dispose() { }
}

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
        string _dir = ""; int _n; readonly Stopwatch _sw = new(); DispatcherTimer? _t;
        readonly List<string> _lines = new(); readonly List<Task> _saves = new();
        static readonly D.Imaging.ImageCodecInfo Jpeg = D.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == D.Imaging.ImageFormat.Jpeg.Guid);

        public void Start(Window w, string scene)
        {
            Stop();
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
            GetWindowRect(h, out var r);
            var bmp = new D.Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), D.Imaging.PixelFormat.Format32bppArgb);
            using (var g = D.Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
            int ox = 0, oy = 0; var frame = bmp;
            if (DwmGetWindowAttribute(h, 9, out var ef, Marshal.SizeOf<RECT>()) == 0)
            {
                var crop = D.Rectangle.Intersect(new D.Rectangle(ef.L - r.L, ef.T - r.T, ef.R - ef.L, ef.B - ef.T), new D.Rectangle(0, 0, bmp.Width, bmp.Height));
                if (crop.Width > 10 && crop.Height > 10) { frame = bmp.Clone(crop, bmp.PixelFormat); bmp.Dispose(); ox = r.L + crop.X; oy = r.T + crop.Y; }
            }
            var p = new POINT(); ClientToScreen(h, ref p);
            var dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
            float cx = (float)(p.X - ox + Cursor.X * dpi), cy = (float)(p.Y - oy + Cursor.Y * dpi);
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

    static void Finish()
    {
        try { Rec.Stop(); } catch { }
        File.WriteAllLines(Path.Combine(_out, "showcase.log"), Log);
        Environment.Exit(0);
    }
}

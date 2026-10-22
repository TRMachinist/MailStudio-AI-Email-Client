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
using MailStudio.Views;
using MailStudio.Views.Calendar;
using MailStudio.Views.Quotes;
using D = System.Drawing;

namespace MailStudio.Services;

/// <summary>
/// `MailStudio.exe --demo --showcase-linkedin <folder>`:
/// Drives MailStudio 1.8 for the premium LinkedIn product film.
/// Captures off-screen at 30 fps (33 ms interval), 1920x1080 resolution,
/// with eased cursor glides, click ripple, and realistic human pacing.
/// Covers 13 continuous chapters:
/// L01-intro, L02-inbox, L03-ai, L04-pdf, L05-dxf, L06-step, L07-revdiff,
/// L08-office, L09-archive, L10-quotes, L11-safety, L12-pim, L13-end.
/// </summary>
public static class ShowcaseLinkedIn
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

    public const int W = 1920, H = 1080;

    static string _out = "";
    static readonly List<string> Log = new();

    // ---------- recorder ----------
    sealed class Recorder
    {
        public Window? Target;
        public Point Cursor = new(W * 0.55, H * 0.55);
        public double Ripple = -1;
        string _dir = "";
        int _n;
        bool _offScreen;
        readonly Stopwatch _sw = new();
        DispatcherTimer? _t;
        readonly List<string> _lines = new();
        readonly List<Task> _saves = new();
        static readonly D.Imaging.ImageCodecInfo Jpeg = D.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == D.Imaging.ImageFormat.Jpeg.Guid);

        public void Start(Window w, string scene)
        {
            Stop();
            _offScreen = WindowCapture.OffScreen;
            Target = w;
            _dir = Path.Combine(_out, scene);
            Directory.CreateDirectory(_dir);
            _n = 0;
            _lines.Clear();
            _sw.Restart();
            // 30 fps capture timer
            _t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
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
            Log.Add($"{Path.GetFileName(_dir)}: {_n} frames, {_sw.ElapsedMilliseconds} ms");
        }

        void Capture()
        {
            var w = Target;
            if (w == null || !w.IsVisible) return;
            var h = new WindowInteropHelper(w).Handle;
            var dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
            D.Bitmap frame;
            float cx, cy;
            if (_offScreen)
            {
                var (rendered, clx, cly) = WindowCapture.Render(w, fresh: false);
                frame = rendered;
                cx = (float)(clx + Cursor.X * dpi);
                cy = (float)(cly + Cursor.Y * dpi);
            }
            else
            {
                GetWindowRect(h, out var r);
                var bmp = new D.Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), D.Imaging.PixelFormat.Format32bppArgb);
                using (var g = D.Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
                int ox = 0, oy = 0;
                frame = bmp;
                if (DwmGetWindowAttribute(h, 9, out var ef, Marshal.SizeOf<RECT>()) == 0)
                {
                    var crop = D.Rectangle.Intersect(new D.Rectangle(ef.L - r.L, ef.T - r.T, ef.R - ef.L, ef.B - ef.T), new D.Rectangle(0, 0, bmp.Width, bmp.Height));
                    if (crop.Width > 10 && crop.Height > 10) { frame = bmp.Clone(crop, bmp.PixelFormat); bmp.Dispose(); ox = r.L + crop.X; oy = r.T + crop.Y; }
                }
                var p = new POINT();
                ClientToScreen(h, ref p);
                cx = (float)(p.X - ox + Cursor.X * dpi);
                cy = (float)(p.Y - oy + Cursor.Y * dpi);
            }
            var ripple = Ripple;
            var file = $"f{_n++:00000}.jpg";
            _lines.Add($"{file}\t{_sw.ElapsedMilliseconds}");
            var path = Path.Combine(_dir, file);
            _saves.Add(Task.Run(() =>
            {
                using (frame)
                {
                    using (var g = D.Graphics.FromImage(frame))
                    {
                        g.SmoothingMode = D.Drawing2D.SmoothingMode.AntiAlias;
                        DrawPointer(g, cx, cy, ripple, (float)dpi);
                    }
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
                var rad = (float)(8 + ripple * 28) * s;
                using var b = new D.SolidBrush(D.Color.FromArgb((int)(160 * (1 - ripple)), 15, 108, 189));
                g.FillEllipse(b, x - rad, y - rad, rad * 2, rad * 2);
            }
            D.PointF[] arrow = { new(0, 0), new(0, 17), new(4.2f, 13.2f), new(7.2f, 20f), new(10f, 18.8f), new(7f, 12.2f), new(12.4f, 12.2f) };
            var pts = arrow.Select(a => new D.PointF(x + a.X * 1.25f * s, y + a.Y * 1.25f * s)).ToArray();
            using var fill = new D.SolidBrush(D.Color.White);
            using var pen = new D.Pen(D.Color.Black, 1.4f * s) { LineJoin = D.Drawing2D.LineJoin.Round };
            g.FillPolygon(fill, pts);
            g.DrawPolygon(pen, pts);
        }
    }

    static readonly Recorder Rec = new();

    // ---------- helpers ----------
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

    /// <summary>Glides the drawn pointer smoothly (cubic ease in-out).</summary>
    static async Task Move(Point to, int ms = 650)
    {
        var from = Rec.Cursor;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var t = sw.ElapsedMilliseconds / (double)ms;
            t = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            Rec.Cursor = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
            await Task.Delay(16);
        }
        Rec.Cursor = to;
    }

    static async Task Move(FrameworkElement e, int ms = 650, double fx = 0.5, double fy = 0.5) =>
        await Move(Center(Rec.Target!, e, fx, fy), ms);

    static async Task ClickFx()
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 320)
        {
            Rec.Ripple = sw.ElapsedMilliseconds / 320.0;
            await Task.Delay(16);
        }
        Rec.Ripple = -1;
    }

    static async Task Click(ButtonBase? b, int moveMs = 650)
    {
        if (b == null) { Log.Add("button not found"); return; }
        await Move(b, moveMs);
        var fx = ClickFx();
        await Task.Delay(100);
        if (b is ToggleButton tb) tb.IsChecked = !(tb.IsChecked == true);
        else b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
        await fx;
    }

    static async Task Type(TextBox box, string text, int charDelayMs = 70)
    {
        await Move(box, 600);
        var fx = ClickFx();
        await Task.Delay(90);
        box.Focus();
        await fx;
        box.Text = "";
        for (int i = 0; i < text.Length; i++)
        {
            box.Text += text[i];
            box.CaretIndex = box.Text.Length;
            await Task.Delay(charDelayMs);
        }
        await Task.Delay(400);
    }

    static async Task<T> WaitNew<T>(HashSet<Window> before, Func<Window, bool>? ok = null, int seconds = 90) where T : Window
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var w = Application.Current.Windows.OfType<T>().LastOrDefault(x => x.IsVisible && !before.Contains(x) && (ok?.Invoke(x) ?? true));
            if (w != null) return w;
            await Task.Delay(120);
        }
        throw new TimeoutException(typeof(T).Name);
    }

    static async Task Until(Func<bool> ok, int seconds = 60)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!ok())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("wait condition timed out");
            await Task.Delay(120);
        }
    }

    static void ForceClose(ComposeWindow c)
    {
        try { typeof(ComposeWindow).GetField("_confirmClose", Any)?.SetValue(c, (Func<MessageBoxResult>)(() => MessageBoxResult.No)); } catch { }
        try { c.Close(); } catch { }
    }

    // ---------- the main LinkedIn showcase runner ----------
    public static async Task RunAsync(MainWindow main, string outDir)
    {
        _out = outDir;
        Directory.CreateDirectory(_out);
        AppSettings.Shared.UndoSendSeconds = 10;

        try
        {
            Place(main);
            await Task.Delay(2500);
            var list = (ListBox)main.FindName("MessageList");
            await Until(() => list.Items.Count >= 9);

            var cache = Field<MailCache>(main, "_cache");
            var all = cache.GetHeaders(DemoData.AccountId, "INBOX", 200);

            MailHeader Msg(uint uid) => list.Items.OfType<MailHeader>().First(m => m.Uid == uid);
            ListBoxItem? Row(uint uid) => list.ItemContainerGenerator.ContainerFromItem(Msg(uid)) as ListBoxItem;
            MailHeader With(string file) => all.First(h => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).Any(a => a.FileName == file));
            AttachmentInfo Att(MailHeader h, string file) => cache.GetAttachments(h.AccountId, h.Folder, h.Uid).First(a => a.FileName == file);
            async Task<string> Download(MailHeader h, string file) => (await (Task<string?>)Call(main, "DownloadAttachmentAsync", h, Att(h, file))!)!;

            async Task Select(uint uid, int pause = 1000)
            {
                if (Row(uid) is { } row) await Move(Center(main, row, 0.45, 0.5), 650);
                var fx = ClickFx();
                await Task.Delay(90);
                list.SelectedItem = Msg(uid);
                await fx;
                await Idle(pause);
            }

            async Task<Window> OpenChip(uint uid, string file, string scene, Func<Window, bool>? ready = null)
            {
                Rec.Start(main, scene + "-a");
                if (list.SelectedItem != Msg(uid)) { list.SelectedItem = Msg(uid); await Idle(1200); }
                var chip = FindEl<ButtonBase>(main, b => b.Tag is AttachmentInfo a && a.FileName == file);
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(chip, 700);
                await Task.Delay(350);
                Rec.Stop();

                var end = DateTime.UtcNow.AddSeconds(120);
                Window? found = null;
                int stable = 0;
                while (DateTime.UtcNow < end)
                {
                    await Task.Delay(600);
                    var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsVisible && !before.Contains(x));
                    if (w != null && w == found && (ready?.Invoke(w) ?? true)) { if (++stable >= 2) break; } else stable = 0;
                    found = w;
                }
                if (found == null) throw new TimeoutException(file);
                Place(found);
                await Idle(2000);
                Rec.Cursor = new Point(W * 0.55, H * 0.5);
                return found;
            }

            // ==========================================
            // CHAPTER 1: L01-intro (0-6s)
            // Logo reveal / app opens full screen on inbox
            // ==========================================
            list.SelectedItem = Msg(1);
            await Idle(1500);
            Rec.Start(main, "L01-intro");
            Rec.Cursor = new Point(W * 0.5, H * 0.45);
            await Move(new Point(W * 0.45, H * 0.35), 900);
            await Task.Delay(2500);
            Rec.Stop();

            // ==========================================
            // CHAPTER 2: L02-inbox (6-14s)
            // Unified inboxes, search folders, select quotation request
            // ==========================================
            Rec.Start(main, "L02-inbox");
            Rec.Cursor = new Point(W * 0.08, H * 0.25);
            var roots = Field<System.Collections.ObjectModel.ObservableCollection<FolderNode>>(main, "_roots");
            var unified = roots.FirstOrDefault(r => r.IsUnifiedInbox);
            if (unified != null)
            {
                await Move(new Point(W * 0.07, H * 0.215), 650);
                Call(main, "SelectFolder", unified);
                await Task.Delay(1100);
            }
            var sf = roots.FirstOrDefault(r => r.IsSearchFolderRoot)?.Children.LastOrDefault();
            if (sf != null)
            {
                await Move(new Point(W * 0.08, H * 0.51), 700);
                Call(main, "SelectFolder", sf);
                await Task.Delay(1200);
            }
            if (unified != null)
            {
                Call(main, "SelectFolder", unified);
                await Idle(800);
            }
            await Select(1, 1400); // Quotation request – flange DN100
            await Move(new Point(W * 0.65, H * 0.35), 800);
            await Task.Delay(1800);
            Rec.Stop();

            // ==========================================
            // CHAPTER 3: L03-ai (14-23s)
            // AI Summarize, then Draft reply, use in reply
            // ==========================================
            Rec.Start(main, "L03-ai");
            await Task.Delay(400);
            var sumBtn = Btn(main, L.T("AI Özet"));
            await Click(sumBtn, 750);
            await Task.Delay(5500); // Offline demo AI response
            var draftBtn = Btn(main, L.T("Cevap taslağı"));
            await Click(draftBtn, 750);
            await Task.Delay(5200);
            var useBtn = Visuals(main).OfType<ButtonBase>().LastOrDefault(b => b.IsVisible && TextOf(b).Contains(ChatMessage.UseAsReplyLabel));
            await Click(useBtn, 750);
            await Task.Delay(3500);
            Rec.Stop();
            // Clean up inline composer
            if (main.GetType().GetField("_inlineCompose", Any)?.GetValue(main) is { } ic) Call(ic, "CloseForTransfer");
            await Idle(1000);

            // ==========================================
            // CHAPTER 4: L04-pdf (23-33s)
            // Flange PDF drawing: zoom, title block inspection
            // ==========================================
            var pdf = await OpenChip(1, "flange-DN100.pdf", "L04-pdf");
            await Idle(2500);
            Rec.Start(pdf, "L04-pdf");
            await Move(new Point(W * 0.48, H * 0.48), 650);
            for (int i = 0; i < 14; i++) { Call(pdf, "SetZoom", 0.6 + i * 0.075, null); await Task.Delay(130); }
            await Move(new Point(W * 0.65, H * 0.6), 750);
            await Task.Delay(1200);
            for (int i = 0; i < 8; i++) { Call(pdf, "SetZoom", 1.65 - i * 0.12, null); await Task.Delay(130); }
            await Task.Delay(900);
            Rec.Stop();
            pdf.Close();
            await Idle(800);

            // ==========================================
            // CHAPTER 5: L05-dxf (33-41s)
            // DXF CAD: layers toggle, measure diameter
            // ==========================================
            var cad = await OpenChip(1, "flange-DN100.dxf", "L05-dxf");
            await Idle(3000);
            Rec.Start(cad, "L05-dxf");
            var canvas = cad.GetType().GetField("_canvas", Any)!.GetValue(cad)!;
            var hatchCheck = FindEl<CheckBox>(cad, c => TextOf(c).Contains("HATCH"));
            if (hatchCheck != null)
            {
                await Click(hatchCheck, 750);
                await Task.Delay(800);
                await Click(hatchCheck, 400);
                await Task.Delay(600);
            }
            await Move(new Point(W * 0.45, H * 0.5), 650);
            for (int i = 0; i < 10; i++) { Call(canvas, "ZoomBy", 1.11); await Task.Delay(110); }
            await Task.Delay(800);
            for (int i = 0; i < 10; i++) { Call(canvas, "ZoomBy", 1 / 1.11); await Task.Delay(90); }
            await Task.Delay(600);
            Rec.Stop();
            cad.Close();
            await Idle(800);

            // ==========================================
            // CHAPTER 6: L06-step (41-50s)
            // STEP model: 3D orbit, bounding box, weight & raw stock
            // ==========================================
            list.SelectedItem = Msg(2);
            await Idle(1200);
            var step = await OpenChip(2, "bracket-revC.stp", "L06-step", w => w.GetType().GetField("_loaded", Any)?.GetValue(w) is true);
            await Idle(2500);
            Rec.Start(step, "L06-step");
            await Move(new Point(W * 0.45, H * 0.48), 600);
            // Smooth 3D orbit
            for (int i = 0; i < 55; i++)
            {
                Call(step, "Orbit", 6.5, i < 28 ? 0.6 : -0.6);
                await Task.Delay(55);
            }
            // Move cursor to property pane (weight / raw stock)
            await Move(new Point(W * 0.84, H * 0.35), 800);
            await Task.Delay(1800);
            Rec.Stop();
            step.Close();
            await Idle(800);

            // ==========================================
            // CHAPTER 7: L07-revdiff (50-62s)
            // Revision comparison: choose rev B, diff list, Add to reply
            // ==========================================
            try
            {
                var revC = With("flange-DN100-revC.dxf");
                list.SelectedItem = list.Items.OfType<MailHeader>().FirstOrDefault(x => x.Uid == revC.Uid);
                await Idle(1500);

                var newPath = await Download(revC, "flange-DN100-revC.dxf");
                var beforeChooser = Application.Current.Windows.OfType<Window>().ToHashSet();
                var pick = main.Dispatcher.InvokeAsync(() => (Task<string?>)Call(main, "PickOlderRevisionAsync", main, newPath)!);
                var chooser = await WaitNew<Window>(beforeChooser, w => w.Title == L.T("Önceki revizyonu seçin"), 20);
                await Idle(800);

                Rec.Start(chooser, "L07a-choose");
                Rec.Cursor = new Point(chooser.ActualWidth * 0.35, chooser.ActualHeight * 0.35);
                var chooserList = FindEl<ListBox>(chooser, _ => true);
                if (chooserList != null)
                {
                    await Move(Center(chooser, chooserList, 0.35, 0.15), 800);
                    await Task.Delay(1200);
                }
                var cmpBtn = Btn(chooser, L.T("Karşılaştır"));
                await Move(cmpBtn!, 700);
                await Task.Delay(600);
                var cfx = ClickFx();
                await Task.Delay(120);
                Rec.Stop();
                cmpBtn!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cmpBtn));
                await cfx;
                var oldPath = await await pick.Task;

                var beforeDiff = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = (Task)Call(main, "ShowRevisionDiffAsync", oldPath!, newPath, revC)!;
                var diff = await WaitNew<RevisionDiffWindow>(beforeDiff, null, 90);
                Place(diff);
                await Idle(1800);

                Rec.Start(diff, "L07b-diff");
                Rec.Cursor = new Point(W * 0.5, H * 0.65);
                var lv = FindEl<ListView>(diff, _ => true);
                if (lv != null)
                {
                    for (int i = 0; i < Math.Min(4, lv.Items.Count); i++)
                    {
                        if (lv.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem it)
                        {
                            await Move(Center(diff, it, 0.35, 0.5), 550);
                            lv.SelectedIndex = i;
                            await Task.Delay(750);
                        }
                    }
                }
                var addReplyBtn = Btn(diff, L.T("Farkı Yanıta Ekle"));
                await Click(addReplyBtn, 700);
                await Task.Delay(900);
                Rec.Stop();
                diff.Close();
                await Idle(2500);

                // Compose window showing the inserted diff table
                Rec.Start(main, "L07c-reply");
                Rec.Cursor = new Point(W * 0.6, H * 0.5);
                await Move(new Point(W * 0.55, H * 0.42), 850);
                await Task.Delay(2600);
                Rec.Stop();

                foreach (var c in main.OwnedWindows.OfType<ComposeWindow>().ToList()) ForceClose(c);
                var inlineComp = Field<ComposeWindow?>(main, "_inlineCompose");
                if (inlineComp != null) ForceClose(inlineComp);
                await Idle(800);
            }
            catch (Exception ex) { Log.Add("FAIL L07: " + ex); }

            // ==========================================
            // CHAPTER 8: L08-office (62-70s)
            // Excel, Word, PowerPoint, CNC (~2s each)
            // ==========================================
            Window Newest() => Application.Current.Windows.OfType<Window>().Last(x => x.IsVisible && x != main);

            // 8a) Excel
            var xlsx = await OpenChip(3, "delivery-schedule-wk43.xlsx", "L08a-excel");
            await Idle(2200);
            Rec.Start(xlsx, "L08a-excel");
            await Move(new Point(W * 0.45, H * 0.25), 650);
            await ClickFx();
            await Move(new Point(W * 0.35, H * 0.32), 650);
            await Task.Delay(1800);
            Rec.Stop();
            xlsx.Close();
            await Idle(600);

            // 8b) Word
            var docx = await OpenChip(4, "FAI-report-NW-1042.docx", "L08b-word");
            await Idle(7500);
            docx = Newest();
            Place(docx);
            await Idle(2000);
            Rec.Start(docx, "L08b-word");
            await Move(new Point(W * 0.48, H * 0.42), 800);
            await Task.Delay(1900);
            Rec.Stop();
            docx.Close();
            await Idle(600);

            // 8c) PowerPoint
            var pptx = await OpenChip(2, "kickoff-bracket-revC.pptx", "L08c-ppt");
            await Idle(7500);
            pptx = Newest();
            Place(pptx);
            await Idle(2000);
            Rec.Start(pptx, "L08c-ppt");
            await Move(new Point(W * 0.35, H * 0.45), 800);
            await Task.Delay(1900);
            Rec.Stop();
            pptx.Close();
            await Idle(600);

            // 8d) CNC
            var nc = await OpenChip(5, "OP20.nc", "L08d-cnc");
            await Idle(1800);
            Rec.Start(nc, "L08d-cnc");
            await Move(new Point(W * 0.38, H * 0.4), 800);
            await Task.Delay(2000);
            Rec.Stop();
            nc.Close();
            await Idle(800);

            // ==========================================
            // CHAPTER 9: L09-archive (70-78s)
            // RAR archive: view files, open drawing directly from archive
            // ==========================================
            try
            {
                list.SelectedItem = Msg(11); // Drawing package – NW-1042 rev C
                await Idle(1200);

                var rarWin = await OpenChip(11, "NW-1042-drawings.rar", "L09a-archive");
                await Idle(2000);
                Rec.Start(rarWin, "L09a-archive");
                Rec.Cursor = new Point(W * 0.4, H * 0.3);

                var archiveList = FindEl<ListView>(rarWin, _ => true);
                if (archiveList != null)
                {
                    // Glide to first entry: flange-DN100-revC.pdf
                    if (archiveList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem item0)
                    {
                        await Move(Center(rarWin, item0, 0.35, 0.5), 750);
                        var lfx = ClickFx();
                        await Task.Delay(90);
                        archiveList.SelectedIndex = 0;
                        await lfx;
                        await Task.Delay(1000);
                    }
                }

                // Click "Open" button in archive viewer toolbar
                var openEntryBtn = Btn(rarWin, L.T("Aç"));
                var beforeInner = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(openEntryBtn, 700);
                await Task.Delay(500);
                Rec.Stop();

                // Wait for inner viewer to open
                var innerPdf = await WaitNew<Window>(beforeInner, null, 30);
                Place(innerPdf);
                await Idle(2000);

                Rec.Start(innerPdf, "L09b-innerpdf");
                Rec.Cursor = new Point(W * 0.5, H * 0.5);
                await Move(new Point(W * 0.45, H * 0.45), 700);
                await Task.Delay(2600);
                Rec.Stop();

                innerPdf.Close();
                await Idle(500);
                rarWin.Close();
                await Idle(800);
            }
            catch (Exception ex) { Log.Add("FAIL L09: " + ex); }

            // ==========================================
            // CHAPTER 10: L10-quotes (78-88s)
            // Quote desk: list, search, status change (Won), Excel export
            // ==========================================
            try
            {
                main.SwitchModule(PimModule.Quotes);
                await Idle(1800);
                var desk = Field<QuoteDeskView?>(main, "_quoteDeskView")!;
                Rec.Start(main, "L10-quotes");
                Rec.Cursor = new Point(W * 0.5, H * 0.55);

                var searchBox = FindEl<TextBox>(main, t => t.IsVisible && t.ActualWidth > 100);
                if (searchBox != null)
                {
                    await Move(searchBox, 700);
                    var sfx = ClickFx();
                    await Task.Delay(90);
                    searchBox.Focus();
                    await sfx;
                    foreach (var ch in "NW-") { desk.SearchText += ch; await Task.Delay(200); }
                    await Task.Delay(900);
                    desk.SearchText = "";
                    await Task.Delay(600);
                }

                var quoteList = Field<ListView>(desk, "_list");
                if (quoteList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem qRow)
                {
                    await Move(Center(main, qRow, 0.35, 0.5), 650);
                    quoteList.SelectedIndex = 0;
                    await Task.Delay(800);
                }

                var wonBtn = Btn(main, L.T("Kazanıldı"));
                await Click(wonBtn, 700);
                await Task.Delay(1400);

                var exportBtn = Btn(main, L.T("Excel'e Aktar"));
                if (exportBtn != null)
                {
                    await Move(exportBtn, 700);
                    await Task.Delay(1200);
                }
                Rec.Stop();
                main.SwitchModule(PimModule.Mail);
                await Idle(800);
            }
            catch (Exception ex) { Log.Add("FAIL L10: " + ex); }

            // ==========================================
            // CHAPTER 11: L11-safety (88-100s)
            // Missing attachment warning, Undo Send, Outbox
            // ==========================================
            try
            {
                var anna = all.First(h => h.FromAddress.StartsWith("anna", StringComparison.OrdinalIgnoreCase));
                var beforeComp = Application.Current.Windows.OfType<Window>().ToHashSet();
                Call(main, "OpenCompose", new ComposeRequest
                {
                    To = anna.FromAddress,
                    Subject = L.T("NW-1042 rev C – quotation"),
                    Body = L.T("Hello Anna,\nPlease find attached our quotation for the revision C flange.")
                }, DemoData.AccountId, true);

                var comp = await WaitNew<ComposeWindow>(beforeComp, null, 25);
                Place(comp);
                await Until(() => comp.IsEditorInitialized, 30);
                await Idle(1500);

                Rec.Start(comp, "L11a-compose");
                Rec.Cursor = new Point(W * 0.45, H * 0.48);
                await Move(new Point(W * 0.35, H * 0.4), 700);
                await Task.Delay(1000);

                var sendBtn = Btn(comp, L.T("Gönder"));
                var beforeMissing = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click(sendBtn, 700);
                await Task.Delay(500);
                Rec.Stop();

                var missingDlg = await WaitNew<Window>(beforeMissing, w => w.Title == L.T("Ek unutulmuş olabilir"), 15);
                await Idle(700);
                Rec.Start(missingDlg, "L11b-missing");
                Rec.Cursor = new Point(missingDlg.ActualWidth * 0.5, missingDlg.ActualHeight * 0.4);
                await Task.Delay(1800);
                var cancelMissing = Btn(missingDlg, L.T("Dosya Ekle")) ?? Btn(missingDlg, L.T("İptal"));
                if (cancelMissing != null) await Move(cancelMissing, 650);
                await Task.Delay(800);
                Rec.Stop();
                missingDlg.Close();
                await Idle(600);

                ForceClose(comp);
                await Idle(600);

                // Safe send with undo bar
                var beforeSafeComp = Application.Current.Windows.OfType<Window>().ToHashSet();
                Call(main, "OpenCompose", new ComposeRequest
                {
                    To = anna.FromAddress,
                    Subject = L.T("NW-1042 rev C confirmation"),
                    Body = L.T("Hello Anna,\nRevision C drawings have been reviewed and approved.")
                }, DemoData.AccountId, true);

                comp = await WaitNew<ComposeWindow>(beforeSafeComp, null, 25);
                Place(comp);
                await Until(() => comp.IsEditorInitialized, 30);
                await Idle(1200);

                Btn(comp, L.T("Gönder"))!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, Btn(comp, L.T("Gönder"))));
                await Until(() => ((FrameworkElement)main.FindName("UndoSendBar")).IsVisible, 15);
                await Idle(500);

                Rec.Start(main, "L11c-undo");
                Rec.Cursor = new Point(W * 0.5, H * 0.55);
                await Task.Delay(1800);
                var beforeUndo = Application.Current.Windows.OfType<Window>().ToHashSet();
                await Click((ButtonBase)main.FindName("UndoSendButton"), 800);
                await Task.Delay(1600);
                Rec.Stop();

                foreach (var c in Application.Current.Windows.OfType<ComposeWindow>().Where(c => !beforeUndo.Contains(c)).ToList()) ForceClose(c);

                // Outbox
                var acc = Field<AccountStore>(main, "_store").Accounts.First();
                main.ScheduleSend(acc, new ComposeRequest { To = "anna.keller@northwind-machining.example", Subject = L.T("Haftalık teslim planı"), Body = "Scheduled update" }, DateTimeOffset.Now.Date.AddDays(1).AddHours(8));
                var beforeOutbox = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = main.Dispatcher.BeginInvoke(() => Call(main, "ShowOutboxWindow"));
                var ow = await WaitNew<OutboxWindow>(beforeOutbox, null, 15);
                await Idle(800);
                Rec.Start(ow, "L11d-outbox");
                Rec.Cursor = new Point(ow.ActualWidth * 0.5, ow.ActualHeight * 0.5);
                await Move(new Point(ow.ActualWidth * 0.35, ow.ActualHeight * 0.25), 700);
                await Task.Delay(1600);
                Rec.Stop();
                ow.Close();
                await Idle(600);
            }
            catch (Exception ex) { Log.Add("FAIL L11: " + ex); }

            // ==========================================
            // CHAPTER 12: L12-pim (100-112s)
            // Follow-up, snooze, calendar week, rules, dark theme
            // ==========================================
            try
            {
                // 12a) Follow-up task
                main.SwitchModule(PimModule.Tasks);
                await Idle(1500);
                Rec.Start(main, "L12a-followup");
                Rec.Cursor = new Point(W * 0.4, H * 0.3);
                var item = FindEl<ListBoxItem>(main, i => i.Tag is Services.Calendar.TaskItem t && t.Category == "Cevap bekleniyor");
                if (item != null)
                {
                    await Move(Center(main, item, 0.3, 0.5), 700);
                    item.IsSelected = true;
                    await Task.Delay(1200);
                }
                var remindBtn = Btn(main, L.T("Hatırlatma maili yaz"));
                if (remindBtn != null) await Move(remindBtn, 700);
                await Task.Delay(1600);
                Rec.Stop();

                // 12b) Snooze
                main.SwitchModule(PimModule.Mail);
                await Idle(1000);
                var snoozeMail = list.Items.OfType<MailHeader>().Skip(2).First();
                Rec.Start(main, "L12b-snooze");
                Rec.Cursor = new Point(W * 0.4, H * 0.5);
                await Select(snoozeMail.Uid, 800);
                var snoozeBtn = Btn(main, L.T("Ertele"));
                if (snoozeBtn != null)
                {
                    await Move(snoozeBtn, 700);
                    var sfx = ClickFx();
                    await Task.Delay(200);
                    await sfx;
                }
                main.SnoozeSelected(DateTimeOffset.Now.Date.AddDays(1).AddHours(9));
                await Task.Delay(1200);
                await Click((ButtonBase)main.FindName("FilterSnoozed"), 750);
                await Task.Delay(1500);
                await Click((ButtonBase)main.FindName("FilterAll"), 650);
                await Task.Delay(600);
                Rec.Stop();

                // 12c) Calendar
                main.SwitchModule(PimModule.Calendar);
                await Idle(1200);
                var cv = Field<CalendarView?>(main, "_calendarView");
                cv?.SetMode(CalendarViewMode.Week);
                await Idle(800);
                Rec.Start(main, "L12c-calendar");
                Rec.Cursor = new Point(W * 0.5, H * 0.5);
                await Move(new Point(W * 0.55, H * 0.45), 700);
                await Task.Delay(1800);
                Rec.Stop();

                // 12d) Rules
                main.SwitchModule(PimModule.Mail);
                await Idle(800);
                var beforeRules = Application.Current.Windows.OfType<Window>().ToHashSet();
                _ = main.Dispatcher.BeginInvoke(() => Call(main, "OnRules", main, new RoutedEventArgs()));
                var rw = await WaitNew<RulesWindow>(beforeRules, null, 20);
                Place(rw);
                await Idle(1200);
                Rec.Start(rw, "L12d-rules");
                Rec.Cursor = new Point(W * 0.25, H * 0.35);
                var lbx = FindEl<ListBox>(rw, _ => true);
                if (lbx?.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem r0)
                {
                    await Move(Center(rw, r0, 0.4, 0.5), 650);
                    await Task.Delay(900);
                }
                await Click(Btn(rw, L.T("Bu kuralı test et")), 800);
                await Task.Delay(2200);
                Rec.Stop();
                rw.Close();
                await Idle(800);

                // 12e) Dark theme toggle
                list.SelectedItem = Msg(1);
                await Idle(1200);
                Rec.Start(main, "L12e-dark");
                Rec.Cursor = new Point(W * 0.5, H * 0.5);
                await Task.Delay(600);
                ((RadioButton)main.FindName("ThemeDark")).IsChecked = true;
                await Idle(400);
                await Task.Delay(800);
                list.SelectedItem = Msg(2);
                await Idle(1600);
                await Task.Delay(1000);
                ((RadioButton)main.FindName("ThemeLight")).IsChecked = true;
                await Idle(400);
                await Task.Delay(700);
                Rec.Stop();
            }
            catch (Exception ex) { Log.Add("FAIL L12: " + ex); }

            // ==========================================
            // CHAPTER 13: L13-end (112-120s)
            // Calm ending / clean inbox overview
            // ==========================================
            list.SelectedItem = Msg(1);
            await Idle(1200);
            Rec.Start(main, "L13-end");
            Rec.Cursor = new Point(W * 0.5, H * 0.5);
            await Move(new Point(W * 0.55, H * 0.45), 800);
            await Task.Delay(3500);
            Rec.Stop();

            Log.Add("OK LinkedIn Showcase");
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
        File.WriteAllLines(Path.Combine(_out, "showcase-linkedin.log"), Log);
        Environment.Exit(0);
    }
}

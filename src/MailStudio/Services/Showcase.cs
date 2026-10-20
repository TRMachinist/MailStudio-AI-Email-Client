using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// `MailStudio.exe --demo --showcase &lt;folder&gt;`: plays a scripted tour of the demo mailbox and saves screenshots and
/// animation frames of MailStudio's own windows (PrintWindow on our window handles only — nothing else on the screen
/// is captured). tools/demo/make_gifs.py turns the frames into the README GIFs.
/// </summary>
public static class Showcase
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;
    static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static string _out = "";

    static async Task Settle(int ms = 350)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
    }

    /// <summary>Saves a PNG of one of our windows (title bar included, invisible resize border cropped).</summary>
    public static void Shot(Window w, string relative)
    {
        var path = Path.Combine(_out, relative + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        w.UpdateLayout();
        var h = new WindowInteropHelper(w).Handle;
        if (WindowCapture.OffScreen)
        {
            using var rendered = WindowCapture.Render(w, fresh: true).Bitmap;
            rendered.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        GetWindowRect(h, out var r);
        using var bmp = new System.Drawing.Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
        var final = bmp;
        if (DwmGetWindowAttribute(h, 9, out var ef, Marshal.SizeOf<RECT>()) == 0)
        {
            var crop = new System.Drawing.Rectangle(ef.L - r.L, ef.T - r.T, ef.R - ef.L, ef.B - ef.T);
            if (crop.Width > 10 && crop.Height > 10) final = bmp.Clone(crop, bmp.PixelFormat);
        }
        final.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        if (!ReferenceEquals(final, bmp)) final.Dispose();
    }

    static async Task Frames(Window w, string name, int count, int everyMs, Func<int, Task>? step = null)
    {
        for (int i = 0; i < count; i++)
        {
            if (step != null) await step(i);
            await Settle(everyMs);
            Shot(w, Path.Combine(name, $"f{i:000}"));
        }
    }

    static void Place(Window w, double width, double height)
    {
        w.WindowState = WindowState.Normal; w.Left = 30; w.Top = 30; w.Width = width; w.Height = height;
    }

    static T? Find<T>() where T : Window => Application.Current.Windows.OfType<T>().LastOrDefault(w => w.IsVisible);

    static async Task<T> WaitWindow<T>(int seconds = 40) where T : Window
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (Find<T>() is { } w) return w; await Task.Delay(200); }
        throw new TimeoutException(typeof(T).Name);
    }

    public static async Task RunAsync(MainWindow main, string outDir)
    {
        _out = outDir; Directory.CreateDirectory(_out);
        var log = new List<string>();
        try
        {
            Place(main, 1440, 900);
            await Task.Delay(2500);
            var list = (ListBox)main.FindName("MessageList");
            await Until(() => list.Items.Count >= 8);
            MailHeader Msg(uint uid) => list.Items.OfType<MailHeader>().First(m => m.Uid == uid);

            // 1) main window still + selection tour
            list.SelectedItem = Msg(1); await Settle(1500);
            Shot(main, "main-window");
            Place(main, 1440, 900); await Settle(600);
            int[] tour = { 1, 2, 3, 4, 5 };
            await Frames(main, "tour", tour.Length * 4, 250, async i => { if (i % 4 == 0) { list.SelectedItem = Msg((uint)tour[i / 4]); await Settle(700); } });

            // 2) AI summary streaming with the offline demo AI
            list.SelectedItem = Msg(1); await Settle(900);
            Call(main, "OnAiSummarize", main, new RoutedEventArgs());
            await Frames(main, "ai-summary", 40, 170);
            await Settle(1500); Shot(main, "ai-summary-done");

            // 3) dark theme still
            ((RadioButton)main.FindName("ThemeDark")).IsChecked = true; await Settle(1200);
            list.SelectedItem = Msg(2); await Settle(1200);
            Shot(main, "main-window-dark");
            ((RadioButton)main.FindName("ThemeLight")).IsChecked = true; await Settle(900);

            // 4) attachments in the built-in viewers
            async Task<Window> OpenAttachment(uint uid, string file, int settleMs = 1500)
            {
                var h = Msg(uid);
                var att = new AttachmentInfo { FileName = file, Part = file };
                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                await (Task)Call(main, "OpenAttachmentAsync", h, att)!;
                var end = DateTime.UtcNow.AddSeconds(90);
                Window? found = null;
                while (DateTime.UtcNow < end)
                {
                    await Task.Delay(settleMs);
                    // Office documents switch windows (viewer -> PDF view / editor): take the newest window that stays open.
                    var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x.IsVisible && !before.Contains(x));
                    if (w != null && w == found) return w;
                    found = w;
                }
                return found ?? throw new TimeoutException(file);
            }

            var pdf = await OpenAttachment(1, "flange-DN100.pdf"); Place(pdf, 1280, 820); await Settle(3500);
            Shot(pdf, "pdf-studio");
            await Frames(pdf, "pdf-zoom", 18, 180, async i => { var z = i < 9 ? 0.55 + i * 0.12 : 1.51 - (i - 9) * 0.1; Call(pdf, "SetZoom", z, null); await Settle(250); });
            pdf.Close();

            var cad = await OpenAttachment(1, "flange-DN100.dxf"); Place(cad, 1280, 820); await Settle(4500);
            Shot(cad, "dxf-viewer");
            var canvas = cad.GetType().GetField("_canvas", Any)!.GetValue(cad)!;
            await Frames(cad, "dxf-zoom", 16, 160, async i => { Call(canvas, "ZoomBy", i < 8 ? 1.18 : 1 / 1.18); await Task.CompletedTask; });
            cad.Close();

            var step = await OpenAttachment(2, "bracket-revC.stp"); Place(step, 1280, 820);
            await Until(() => Field<bool>(step, "_loaded"), 90); await Settle(2500);
            Shot(step, "step-viewer");
            await Frames(step, "step-orbit", 40, 110, async i => { Call(step, "Orbit", 9.0, 0.0); await Task.CompletedTask; });
            step.Close();

            var xlsx = await OpenAttachment(3, "delivery-schedule-wk43.xlsx", 2500); Place(xlsx, 1280, 760); await Settle(3000);
            Shot(xlsx, "excel-editor"); xlsx.Close();

            var docx = await OpenAttachment(4, "FAI-report-NW-1042.docx", 4000); Place(docx, 1280, 860); await Settle(9000);
            Shot(docx, "word-view"); docx.Close();

            var nc = await OpenAttachment(5, "OP20.nc"); Place(nc, 1100, 720); await Settle(1500);
            Shot(nc, "cnc-viewer"); nc.Close();

            var pptx = await OpenAttachment(2, "kickoff-bracket-revC.pptx", 4000); Place(pptx, 1280, 800); await Settle(9000);
            Shot(pptx, "powerpoint-view"); pptx.Close();

            // 5) conditional formatting window
            _ = main.Dispatcher.BeginInvoke(() => Call(main, "OnFormatRules", main, new RoutedEventArgs()));   // modal: keep the tour running
            var rules = await WaitWindow<Views.FormatRulesWindow>(); await Settle(1200);
            Shot(rules, "conditional-formatting"); rules.Close();

            // 6) add-account dialog (provider presets, OAuth, offline settings)
            _ = main.Dispatcher.BeginInvoke(() => Call(main, "OnAddAccount", main, new RoutedEventArgs()));
            var accWin = await WaitWindow<Views.AccountWindow>(); await Settle(1200);
            Shot(accWin, "account-window"); accWin.Close();

            // 7) reply with AI draft
            Place(main, 1440, 900); await Settle(500);
            list.SelectedItem = Msg(1); await Settle(900);
            Call(main, "OnReply", main, new RoutedEventArgs()); await Settle(4000);
            Shot(main, "reply-inline");
            log.Add("OK");
        }
        catch (Exception ex) { log.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(_out, "showcase.log"), log);
        Environment.Exit(0);   // demo data only: nothing to save
    }

    static async Task Until(Func<bool> ok, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!ok()) { if (DateTime.UtcNow > end) throw new TimeoutException("showcase wait"); await Task.Delay(150); }
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using D = System.Drawing;

namespace MailStudio.Services;

/// <summary>
/// Pictures of MailStudio's own windows for the self-test and the demo recordings.
/// On the visible desktop Windows draws the window (PrintWindow). When the tests run on a separate, hidden desktop
/// (tools\selftest\Start-OnHiddenDesktop.ps1) or the screen is locked, nothing is drawn there - then WPF renders the
/// window itself and the mail view (WebView2) is added from its own preview capture.
/// </summary>
public static class WindowCapture
{
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int index, [Out] char[]? info, int length, out int needed);

    static string DesktopName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        GetUserObjectInformation(h, 2, null, 0, out var needed);   // UOI_NAME
        if (needed <= 0) return "";
        var buf = new char[needed / 2 + 1];
        return GetUserObjectInformation(h, 2, buf, buf.Length * 2, out _) ? new string(buf).TrimEnd('\0') : "";
    }

    /// <summary>True when our windows are not on the desktop the user sees (hidden test desktop, locked screen).</summary>
    public static bool OffScreen
    {
        get
        {
            var mine = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            var input = OpenInputDesktop(0, false, 0x0001);   // DESKTOP_READOBJECTS
            try { return input == IntPtr.Zero || !string.Equals(mine, DesktopName(input), StringComparison.OrdinalIgnoreCase); }
            finally { if (input != IntPtr.Zero) CloseDesktop(input); }
        }
    }

    // ---------- WebView2 pictures (they are separate native windows, WPF cannot render them) ----------
    sealed class WebShot { public D.Bitmap? Bitmap; public bool Busy; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebView2, WebShot> Web = new();

    static void Refresh(WebView2 wv, WebShot shot, Action? done = null)
    {
        if (shot.Busy || wv.CoreWebView2 == null) { done?.Invoke(); return; }
        shot.Busy = true;
        var ms = new MemoryStream();
        _ = wv.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms).ContinueWith(t =>
        {
            try { if (t.IsCompletedSuccessfully && ms.Length > 0) { ms.Position = 0; var b = new D.Bitmap(ms); var old = shot.Bitmap; shot.Bitmap = b; old?.Dispose(); } }
            catch { }
            finally { shot.Busy = false; done?.Invoke(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Renders the window with WPF (client area plus a drawn title bar). fresh: wait (max 3 s, the UI keeps running)
    /// for a new picture of every mail view; otherwise the last one is used and a new one is requested.
    /// Returns the bitmap and where the client area starts in it.
    /// </summary>
    public static (D.Bitmap Bitmap, int ClientX, int ClientY) Render(Window w, bool fresh)
    {
        w.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(w);
        var root = w.Content as FrameworkElement ?? (FrameworkElement)w;
        int cw = Math.Max(1, (int)Math.Round(root.ActualWidth * dpi.DpiScaleX)), ch = Math.Max(1, (int)Math.Round(root.ActualHeight * dpi.DpiScaleY));
        int title = (int)Math.Round(31 * dpi.DpiScaleY);

        var webs = new List<(WebView2 View, WebShot Shot)>();
        foreach (var wv in Descendants(root).OfType<WebView2>().Where(v => v.IsVisible && v.ActualWidth > 0))
            webs.Add((wv, Web.GetOrCreateValue(wv)));
        if (fresh && webs.Count > 0)
        {
            var left = webs.Count;
            var frame = new DispatcherFrame();
            foreach (var (v, s) in webs) Refresh(v, s, () => { if (--left <= 0) frame.Continue = false; });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            if (left > 0) Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        else foreach (var (v, s) in webs) Refresh(v, s);

        var rtb = new RenderTargetBitmap(cw, ch, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        rtb.Render(bg);
        rtb.Render(root);

        var bmp = new D.Bitmap(cw, ch + title, D.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new D.Rectangle(0, title, cw, ch), D.Imaging.ImageLockMode.WriteOnly, D.Imaging.PixelFormat.Format32bppArgb);
        try { rtb.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * ch, data.Stride); }
        finally { bmp.UnlockBits(data); }

        using (var g = D.Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = D.Text.TextRenderingHint.ClearTypeGridFit;
            var dark = ThemeManager.IsDark;
            var barColor = dark ? D.Color.FromArgb(32, 32, 32) : D.Color.FromArgb(243, 243, 243);
            var textColor = dark ? D.Color.FromArgb(230, 230, 230) : D.Color.FromArgb(32, 32, 32);
            using (var bar = new D.SolidBrush(barColor)) g.FillRectangle(bar, 0, 0, cw, title);
            using var font = new D.Font("Segoe UI", 9f * (float)dpi.DpiScaleY, D.GraphicsUnit.Point);
            using var text = new D.SolidBrush(textColor);
            var fmt = new D.StringFormat { LineAlignment = D.StringAlignment.Center, Trimming = D.StringTrimming.EllipsisCharacter, FormatFlags = D.StringFormatFlags.NoWrap };
            g.DrawString(w.Title ?? "", font, text, new D.RectangleF(12 * (float)dpi.DpiScaleX, 0, cw - 160 * (float)dpi.DpiScaleX, title), fmt);
            using var glyphs = new D.Font("Segoe MDL2 Assets", 7.5f * (float)dpi.DpiScaleY, D.GraphicsUnit.Point);
            var cap = new[] { "", w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? "" : "", "" };
            float bw = 46 * (float)dpi.DpiScaleX;
            for (int i = 0; i < 3; i++)
                if (cap[i].Length > 0)
                    g.DrawString(cap[i], glyphs, text, new D.RectangleF(cw - (3 - i) * bw, 0, bw, title), new D.StringFormat { Alignment = D.StringAlignment.Center, LineAlignment = D.StringAlignment.Center });

            foreach (var (v, s) in webs)
            {
                if (s.Bitmap == null) continue;
                try
                {
                    var p = v.TransformToAncestor(root).Transform(new Point(0, 0));
                    var dest = new D.Rectangle((int)Math.Round(p.X * dpi.DpiScaleX), title + (int)Math.Round(p.Y * dpi.DpiScaleY),
                                               (int)Math.Round(v.ActualWidth * dpi.DpiScaleX), (int)Math.Round(v.ActualHeight * dpi.DpiScaleY));
                    // 1:1, never stretched: right after a resize the last picture may still have the old size
                    using (var white = new D.SolidBrush(D.Color.White)) g.FillRectangle(white, dest);
                    var src = new D.Rectangle(0, 0, Math.Min(dest.Width, s.Bitmap.Width), Math.Min(dest.Height, s.Bitmap.Height));
                    g.DrawImage(s.Bitmap, new D.Rectangle(dest.X, dest.Y, src.Width, src.Height), src, D.GraphicsUnit.Pixel);
                }
                catch { }
            }
        }
        return (bmp, 0, title);
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop(); yield return d;
            var n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (var i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }
}

// Captures the top-level windows of ONE process with PrintWindow (works for WebView2/DirectComposition content and
// never reads anything else on the screen). Used to make README screenshots from the demo mailbox.
//   wincap <pid> <out-folder> [width height]   -> saves every visible window of the process as PNG
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static int Main(string[] a)
    {
        var pid = uint.Parse(a[0]); var dir = a[1]; Directory.CreateDirectory(dir);
        int? w = a.Length > 3 ? int.Parse(a[2]) : null, hgt = a.Length > 3 ? int.Parse(a[3]) : null;
        var list = new List<(IntPtr h, string title)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (sb.Length > 0) list.Add((h, sb.ToString())); }
            return true;
        }, IntPtr.Zero);
        int i = 0;
        foreach (var (h, title) in list)
        {
            if (w.HasValue) { SetWindowPos(h, IntPtr.Zero, 40, 40, w.Value, hgt!.Value, 0x0004 | 0x0010); Thread.Sleep(900); }
            GetWindowRect(h, out var r);
            using var bmp = new Bitmap(r.R - r.L, r.B - r.T, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
            // crop the invisible resize border (DWM extended frame)
            Bitmap final = bmp;
            if (DwmGetWindowAttribute(h, 9, out var ef, Marshal.SizeOf<RECT>()) == 0)
            {
                var crop = new Rectangle(ef.L - r.L, ef.T - r.T, ef.R - ef.L, ef.B - ef.T);
                if (crop.Width > 0 && crop.Height > 0) final = bmp.Clone(crop, bmp.PixelFormat);
            }
            var name = Path.Combine(dir, $"{i++:00}-{string.Concat(title.Where(char.IsLetterOrDigit)).PadRight(1)[..Math.Min(30, string.Concat(title.Where(char.IsLetterOrDigit)).Length)]}.png");
            final.Save(name, ImageFormat.Png);
            Console.WriteLine(name + "  " + title);
        }
        return list.Count > 0 ? 0 : 1;
    }
}

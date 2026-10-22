from pathlib import Path

p = Path(r"D:\MailStudio_GitHub\src\MailStudio\Services\WindowCapture.cs")
c = p.read_text(encoding="utf-8")
c_norm = c.replace("\r\n", "\n")

old_sig = """    public static (D.Bitmap Bitmap, int ClientX, int ClientY) Render(Window w, bool fresh)
    {
        w.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(w);
        // The window template's root fills the client area at (0,0); rendering w.Content would shift and crop it by its margin
        var root = (VisualTreeHelper.GetChildrenCount(w) > 0 ? VisualTreeHelper.GetChild(w, 0) as FrameworkElement : null) ?? w.Content as FrameworkElement ?? w;
        int cw = Math.Max(1, (int)Math.Round(root.ActualWidth * dpi.DpiScaleX)), ch = Math.Max(1, (int)Math.Round(root.ActualHeight * dpi.DpiScaleY));
        int title = (int)Math.Round(31 * dpi.DpiScaleY);"""

new_sig = """    public static (D.Bitmap Bitmap, int ClientX, int ClientY) Render(Window w, bool fresh, double scale = 1.0)
    {
        w.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(w);
        // The window template's root fills the client area at (0,0); rendering w.Content would shift and crop it by its margin
        var root = (VisualTreeHelper.GetChildrenCount(w) > 0 ? VisualTreeHelper.GetChild(w, 0) as FrameworkElement : null) ?? w.Content as FrameworkElement ?? w;
        int cw = Math.Max(1, (int)Math.Round(root.ActualWidth * dpi.DpiScaleX * scale)), ch = Math.Max(1, (int)Math.Round(root.ActualHeight * dpi.DpiScaleY * scale));
        int title = (int)Math.Round(31 * dpi.DpiScaleY * scale);"""

assert old_sig in c_norm, "old_sig not found"
c_norm = c_norm.replace(old_sig, new_sig, 1)

old_rtb = "var rtb = new RenderTargetBitmap(cw, ch, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);"
new_rtb = "var rtb = new RenderTargetBitmap(cw, ch, 96 * dpi.DpiScaleX * scale, 96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);"
assert old_rtb in c_norm, "old_rtb not found"
c_norm = c_norm.replace(old_rtb, new_rtb, 1)

old_font = 'using var font = new D.Font("Segoe UI", 9f * (float)dpi.DpiScaleY, D.GraphicsUnit.Point);'
new_font = 'using var font = new D.Font("Segoe UI", 9f * (float)(dpi.DpiScaleY * scale), D.GraphicsUnit.Point);'
assert old_font in c_norm, "old_font not found"
c_norm = c_norm.replace(old_font, new_font, 1)

old_rectf = "new D.RectangleF(12 * (float)dpi.DpiScaleX, 0, cw - 160 * (float)dpi.DpiScaleX, title)"
new_rectf = "new D.RectangleF(12 * (float)(dpi.DpiScaleX * scale), 0, cw - 160 * (float)(dpi.DpiScaleX * scale), title)"
assert old_rectf in c_norm, "old_rectf not found"
c_norm = c_norm.replace(old_rectf, new_rectf, 1)

old_glyph = 'using var glyphs = new D.Font("Segoe MDL2 Assets", 7.5f * (float)dpi.DpiScaleY, D.GraphicsUnit.Point);'
new_glyph = 'using var glyphs = new D.Font("Segoe MDL2 Assets", 7.5f * (float)(dpi.DpiScaleY * scale), D.GraphicsUnit.Point);'
assert old_glyph in c_norm, "old_glyph not found"
c_norm = c_norm.replace(old_glyph, new_glyph, 1)

old_bw = "float bw = 46 * (float)dpi.DpiScaleX;"
new_bw = "float bw = 46 * (float)(dpi.DpiScaleX * scale);"
assert old_bw in c_norm, "old_bw not found"
c_norm = c_norm.replace(old_bw, new_bw, 1)

old_dest = "var dest = new D.Rectangle((int)Math.Round(p.X * dpi.DpiScaleX), title + (int)Math.Round(p.Y * dpi.DpiScaleY),\n                                               (int)Math.Round(v.ActualWidth * dpi.DpiScaleX), (int)Math.Round(v.ActualHeight * dpi.DpiScaleY));"
new_dest = "var dest = new D.Rectangle((int)Math.Round(p.X * dpi.DpiScaleX * scale), title + (int)Math.Round(p.Y * dpi.DpiScaleY * scale),\n                                               (int)Math.Round(v.ActualWidth * dpi.DpiScaleX * scale), (int)Math.Round(v.ActualHeight * dpi.DpiScaleY * scale));"
assert old_dest in c_norm, "old_dest not found"
c_norm = c_norm.replace(old_dest, new_dest, 1)

p.write_text(c_norm.replace("\n", "\r\n"), encoding="utf-8")
print("WindowCapture.cs successfully updated with scale support.")

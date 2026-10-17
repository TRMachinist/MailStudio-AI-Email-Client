using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views.Editors;

/// <summary>How a cell is shown on screen.</summary>
public sealed class CellView
{
    public string Text = "";
    public bool Bold, Italic, Underline, Strike, Wrap, IsNumber;
    public Color? Fore, Fill;
    public TextAlignment? Align;
    public double FontSize = 11;
    public string? FontName;
}

/// <summary>
/// Virtualized table view: draws only the cells on screen (like Excel). Fixed row/column headers,
/// click to select, Shift to extend, drag for a range, click a header to select a row/column, drag a column edge to resize.
/// </summary>
public sealed class SheetView : FrameworkElement
{
    public const double HeaderW = 46, HeaderH = 22;
    public Func<int, int, CellView?> GetCell = (_, _) => null;
    public Func<int, double> ColWidth = _ => 72;
    public Func<int, double> RowHeight = _ => 20;
    public Func<IEnumerable<(int r1, int c1, int r2, int c2)>> Merges = () => Array.Empty<(int, int, int, int)>();
    public Action<int, double>? ColumnResized;
    public int MaxRow = 1000, MaxCol = 52;
    public int ActiveRow = 1, ActiveCol = 1, AnchorRow = 1, AnchorCol = 1;
    public double OffsetX, OffsetY;
    public event Action? SelectionChanged, ViewChanged;
    public event Action<bool>? EditRequested;

    double[] _colX = Array.Empty<double>(), _rowY = Array.Empty<double>();
    Point? _dragFrom; bool _selectingCols, _selectingRows; int _resizeCol = -1; double _resizeStartX, _resizeStartW;
    readonly Typeface _ui = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public SheetView() { Focusable = true; FocusVisualStyle = null; ClipToBounds = true; Cursor = Cursors.Cross; SnapsToDevicePixels = true; }

    public void Rebuild()
    {
        _colX = new double[MaxCol + 2]; for (int c = 1; c <= MaxCol + 1; c++) _colX[c] = _colX[c - 1] + (c - 1 >= 1 ? ColWidth(c - 1) : 0);
        _rowY = new double[MaxRow + 2]; for (int r = 1; r <= MaxRow + 1; r++) _rowY[r] = _rowY[r - 1] + (r - 1 >= 1 ? RowHeight(r - 1) : 0);
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    public double TotalWidth => _colX.Length > 0 ? _colX[^1] : 0;
    public double TotalHeight => _rowY.Length > 0 ? _rowY[^1] : 0;
    double X(int c) => _colX[Math.Clamp(c, 1, MaxCol + 1)];
    double Y(int r) => _rowY[Math.Clamp(r, 1, MaxRow + 1)];

    public int Top => Lower(_rowY, OffsetY);
    public int Left => Lower(_colX, OffsetX);
    static int Lower(double[] a, double v)
    {
        int lo = 1, hi = a.Length - 2; if (hi < 1) return 1;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (a[mid] <= v) lo = mid; else hi = mid - 1; }
        return lo;
    }

    public Rect CellRect(int r, int c) => new(HeaderW + X(c) - OffsetX, HeaderH + Y(r) - OffsetY, X(c + 1) - X(c), Y(r + 1) - Y(r));

    public (int r1, int c1, int r2, int c2) Selection => (Math.Min(AnchorRow, ActiveRow), Math.Min(AnchorCol, ActiveCol), Math.Max(AnchorRow, ActiveRow), Math.Max(AnchorCol, ActiveCol));

    public void Select(int r, int c, bool extend)
    {
        ActiveRow = Math.Clamp(r, 1, MaxRow); ActiveCol = Math.Clamp(c, 1, MaxCol);
        if (!extend) { AnchorRow = ActiveRow; AnchorCol = ActiveCol; }
        EnsureVisible(ActiveRow, ActiveCol); InvalidateVisual(); SelectionChanged?.Invoke();
    }

    public void EnsureVisible(int r, int c)
    {
        var w = ActualWidth - HeaderW; var h = ActualHeight - HeaderH;
        if (w <= 20 || h <= 20) return;   // no layout yet
        if (Y(r) < OffsetY) OffsetY = Y(r); else if (Y(r + 1) > OffsetY + h) OffsetY = Math.Max(0, Y(r + 1) - h);
        if (X(c) < OffsetX) OffsetX = X(c); else if (X(c + 1) > OffsetX + w) OffsetX = Math.Max(0, X(c + 1) - w);
        ViewChanged?.Invoke();
    }

    public static string ColName(int c) { var s = ""; while (c > 0) { c--; s = (char)('A' + c % 26) + s; c /= 26; } return s; }

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var bg = ThemeManager.IsDark ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White;
        var gridColor = ThemeManager.IsDark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE1, 0xE1, 0xE1);
        var headBg = ThemeManager.IsDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        var headSel = ThemeManager.IsDark ? Color.FromRgb(0x3D, 0x4A, 0x5C) : Color.FromRgb(0xD3, 0xE3, 0xF5);
        var textColor = ThemeManager.IsDark ? Color.FromRgb(0xEE, 0xEE, 0xEE) : Color.FromRgb(0x1F, 0x1F, 0x1F);
        var accent = Color.FromRgb(0x10, 0x7C, 0x41);
        var gridPen = new Pen(new SolidColorBrush(gridColor), 1); gridPen.Freeze();
        dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_colX.Length == 0) return;
        int r0 = Top, c0 = Left;
        int r1 = r0; while (r1 <= MaxRow && Y(r1) - OffsetY < ActualHeight - HeaderH) r1++;
        int c1 = c0; while (c1 <= MaxCol && X(c1) - OffsetX < ActualWidth - HeaderW) c1++;
        var sel = Selection;
        var merges = Merges().Where(m => m.r2 >= r0 && m.r1 <= r1 && m.c2 >= c0 && m.c1 <= c1).ToList();
        bool InMerge(int r, int c, out (int r1, int c1, int r2, int c2) m) { foreach (var x in merges) if (r >= x.r1 && r <= x.r2 && c >= x.c1 && c <= x.c2) { m = x; return true; } m = default; return false; }

        dc.PushClip(new RectangleGeometry(new Rect(HeaderW, HeaderH, Math.Max(0, ActualWidth - HeaderW), Math.Max(0, ActualHeight - HeaderH))));
        // dolgu
        for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
            {
                if (InMerge(r, c, out var m) && (r != m.r1 || c != m.c1)) continue;
                var v = GetCell(r, c); var rect = InMerge(r, c, out m) ? Span(m) : CellRect(r, c);
                if (v?.Fill is { } f) dc.DrawRectangle(new SolidColorBrush(f), null, rect);
            }
        // grid
        for (int c = c0; c <= c1; c++) { var x = Math.Round(HeaderW + X(c) - OffsetX) + .5; dc.DrawLine(gridPen, new Point(x, HeaderH), new Point(x, ActualHeight)); }
        for (int r = r0; r <= r1; r++) { var y = Math.Round(HeaderH + Y(r) - OffsetY) + .5; dc.DrawLine(gridPen, new Point(HeaderW, y), new Point(ActualWidth, y)); }
        foreach (var m in merges)
        {
            var v = GetCell(m.r1, m.c1); var rect = Span(m); rect.Inflate(-1, -1);
            dc.DrawRectangle(new SolidColorBrush(v?.Fill ?? bg), null, rect);
        }
        // metin
        for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
            {
                var merged = InMerge(r, c, out var m);
                if (merged && (r != m.r1 || c != m.c1)) continue;
                var v = GetCell(r, c); if (v == null || v.Text.Length == 0) continue;
                var rect = merged ? Span(m) : CellRect(r, c);
                var weight = v.Bold ? FontWeights.Bold : FontWeights.Normal; var style = v.Italic ? FontStyles.Italic : FontStyles.Normal;
                var face = new Typeface(new FontFamily(v.FontName ?? "Calibri, Segoe UI"), style, weight, FontStretches.Normal);
                var size = Math.Clamp(v.FontSize, 6, 48) * 96 / 72;
                var ft = new FormattedText(v.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(v.Fore ?? textColor), dpi)
                { MaxLineCount = v.Wrap ? int.MaxValue : 1, Trimming = TextTrimming.None };
                if (v.Wrap) ft.MaxTextWidth = Math.Max(4, rect.Width - 6);
                var deco = new TextDecorationCollection(); if (v.Underline) deco.Add(TextDecorations.Underline); if (v.Strike) deco.Add(TextDecorations.Strikethrough);
                if (deco.Count > 0) ft.SetTextDecorations(deco);
                var align = v.Align ?? (v.IsNumber ? TextAlignment.Right : TextAlignment.Left);
                var x = align switch { TextAlignment.Right => rect.Right - ft.WidthIncludingTrailingWhitespace - 3, TextAlignment.Center => rect.X + (rect.Width - ft.Width) / 2, _ => rect.X + 3 };
                var y = rect.Bottom - Math.Min(ft.Height, rect.Height) - 2; if (v.Wrap) y = rect.Y + 2;
                // overflowing text: spills over into the next cell if it is empty (like Excel); numbers do not spill
                var clip = rect;
                if (!v.Wrap && align == TextAlignment.Left && !v.IsNumber && ft.Width > rect.Width - 4)
                {
                    var cc = c + 1; while (cc <= MaxCol && GetCell(r, cc) is not { Text.Length: > 0 } && X(cc) - X(c) < ft.Width + 6) cc++;
                    clip = new Rect(rect.X, rect.Y, X(cc) - X(c), rect.Height);
                }
                dc.PushClip(new RectangleGeometry(clip)); dc.DrawText(ft, new Point(x, y)); dc.Pop();
            }
        // selection
        var selRect = Rect.Union(CellRect(sel.r1, sel.c1), CellRect(sel.r2, sel.c2));
        var selFill = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B));
        if (sel.r1 != sel.r2 || sel.c1 != sel.c2) dc.DrawRectangle(selFill, null, selRect);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(accent), 2), selRect);
        var act = CellRect(ActiveRow, ActiveCol);
        dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(selRect.Right - 3, selRect.Bottom - 3, 6, 6));
        dc.Pop();

        // headers
        var headBrush = new SolidColorBrush(headBg); var headSelBrush = new SolidColorBrush(headSel); var headText = new SolidColorBrush(ThemeManager.IsDark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x44, 0x44, 0x44));
        dc.DrawRectangle(headBrush, null, new Rect(0, 0, ActualWidth, HeaderH));
        dc.DrawRectangle(headBrush, null, new Rect(0, 0, HeaderW, ActualHeight));
        dc.PushClip(new RectangleGeometry(new Rect(HeaderW, 0, Math.Max(0, ActualWidth - HeaderW), HeaderH)));
        for (int c = c0; c < c1; c++)
        {
            var rect = new Rect(HeaderW + X(c) - OffsetX, 0, X(c + 1) - X(c), HeaderH);
            if (c >= sel.c1 && c <= sel.c2) dc.DrawRectangle(headSelBrush, null, rect);
            var x = Math.Round(rect.Right) + .5; dc.DrawLine(gridPen, new Point(x, 0), new Point(x, HeaderH));
            var ft = new FormattedText(ColName(c), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 11.5, headText, dpi);
            dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, (HeaderH - ft.Height) / 2));
        }
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(0, HeaderH, HeaderW, Math.Max(0, ActualHeight - HeaderH))));
        for (int r = r0; r < r1; r++)
        {
            var rect = new Rect(0, HeaderH + Y(r) - OffsetY, HeaderW, Y(r + 1) - Y(r));
            if (r >= sel.r1 && r <= sel.r2) dc.DrawRectangle(headSelBrush, null, rect);
            var y = Math.Round(rect.Bottom) + .5; dc.DrawLine(gridPen, new Point(0, y), new Point(HeaderW, y));
            var ft = new FormattedText(r.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 11.5, headText, dpi);
            dc.DrawText(ft, new Point((HeaderW - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
        }
        dc.Pop();
        dc.DrawLine(gridPen, new Point(0, HeaderH - .5), new Point(ActualWidth, HeaderH - .5));
        dc.DrawLine(gridPen, new Point(HeaderW - .5, 0), new Point(HeaderW - .5, ActualHeight));
        _ = act;
    }

    Rect Span((int r1, int c1, int r2, int c2) m) => Rect.Union(CellRect(m.r1, m.c1), CellRect(m.r2, m.c2));

    (int r, int c) Hit(Point p)
    {
        var c = Lower(_colX, Math.Max(0, p.X - HeaderW + OffsetX)); var r = Lower(_rowY, Math.Max(0, p.Y - HeaderH + OffsetY));
        return (Math.Clamp(r, 1, MaxRow), Math.Clamp(c, 1, MaxCol));
    }

    int ColumnEdge(Point p)
    {
        if (p.Y > HeaderH || p.X < HeaderW) return -1;
        for (int c = Left; c <= MaxCol; c++) { var x = HeaderW + X(c + 1) - OffsetX; if (Math.Abs(p.X - x) <= 3) return c; if (x > p.X + 4) break; }
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); var p = e.GetPosition(this);
        var edge = ColumnEdge(p);
        if (edge > 0) { _resizeCol = edge; _resizeStartX = p.X; _resizeStartW = ColWidth(edge); CaptureMouse(); e.Handled = true; return; }
        var (r, c) = Hit(p);
        _selectingCols = p.Y < HeaderH && p.X > HeaderW; _selectingRows = p.X < HeaderW && p.Y > HeaderH;
        if (p.X < HeaderW && p.Y < HeaderH) { AnchorRow = 1; AnchorCol = 1; ActiveRow = MaxRow; ActiveCol = MaxCol; InvalidateVisual(); SelectionChanged?.Invoke(); return; }
        if (_selectingCols) { if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { AnchorCol = c; AnchorRow = 1; } ActiveCol = c; ActiveRow = MaxRow; }
        else if (_selectingRows) { if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { AnchorRow = r; AnchorCol = 1; } ActiveRow = r; ActiveCol = MaxCol; }
        else if (e.ClickCount == 2) { Select(r, c, false); EditRequested?.Invoke(false); return; }
        else { ActiveRow = r; ActiveCol = c; if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { AnchorRow = r; AnchorCol = c; } }
        _dragFrom = p; CaptureMouse(); InvalidateVisual(); SelectionChanged?.Invoke(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_resizeCol > 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            var w = Math.Max(8, _resizeStartW + p.X - _resizeStartX); ColumnResized?.Invoke(_resizeCol, w); return;
        }
        Cursor = ColumnEdge(p) > 0 ? Cursors.SizeWE : p.X < HeaderW || p.Y < HeaderH ? Cursors.Arrow : Cursors.Cross;
        if (_dragFrom == null || e.LeftButton != MouseButtonState.Pressed) return;
        var (r, c) = Hit(p);
        if (_selectingCols) ActiveCol = c; else if (_selectingRows) ActiveRow = r; else { ActiveRow = r; ActiveCol = c; }
        if (p.Y > ActualHeight - 8) OffsetY += 20; if (p.X > ActualWidth - 8) OffsetX += 30;
        if (p.Y < HeaderH + 4 && OffsetY > 0) OffsetY = Math.Max(0, OffsetY - 20);
        InvalidateVisual(); SelectionChanged?.Invoke(); ViewChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _dragFrom = null; _resizeCol = -1; ReleaseMouseCapture(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) OffsetX = Math.Max(0, OffsetX - e.Delta * .6);
        else OffsetY = Math.Max(0, OffsetY - e.Delta * .6);
        InvalidateVisual(); ViewChanged?.Invoke(); e.Handled = true;
    }
}

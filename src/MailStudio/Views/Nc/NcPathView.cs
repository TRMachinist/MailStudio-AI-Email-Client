using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services.Nc;

namespace MailStudio.Views.Nc;

public enum NcProjection { Top, Front, Side, Iso }

/// <summary>Draws one or two tool paths (backplot) in a chosen projection; wheel zooms at the cursor, drag pans,
/// a click selects the nearest path segment.</summary>
public sealed class NcPathView : FrameworkElement
{
    NcProgram? _program, _other;
    NcProjection _projection = NcProjection.Iso;
    double _zoom = 1, _panX, _panY;
    Point? _drag; Point _dragPan;
    int _upTo = int.MaxValue;
    int _selectedLine = -1;
    bool _showRapids = true, _byTool;

    /// <summary>Raised with the program line (0-based) of the clicked segment.</summary>
    public event Action<int>? LineClicked;

    static readonly Color[] ToolColors =
    {
        Color.FromRgb(15, 108, 189), Color.FromRgb(16, 150, 64), Color.FromRgb(160, 80, 200), Color.FromRgb(214, 120, 0),
        Color.FromRgb(0, 150, 160), Color.FromRgb(190, 40, 120), Color.FromRgb(90, 110, 30), Color.FromRgb(120, 80, 40)
    };

    public NcPathView() { ClipToBounds = true; Focusable = true; Cursor = Cursors.Hand; }

    public void SetPrograms(NcProgram program, NcProgram? other = null) { _program = program; _other = other; _upTo = int.MaxValue; Fit(); }
    public NcProjection Projection { get => _projection; set { _projection = value; Fit(); } }
    public bool ShowRapids { get => _showRapids; set { _showRapids = value; InvalidateVisual(); } }
    public bool ColorByTool { get => _byTool; set { _byTool = value; InvalidateVisual(); } }
    /// <summary>Only segments up to this index are drawn (simulation slider).</summary>
    public int UpTo { get => _upTo; set { _upTo = value; InvalidateVisual(); } }
    public int SelectedLine { get => _selectedLine; set { _selectedLine = value; InvalidateVisual(); } }
    public static Color ColorOfTool(int index) => ToolColors[Math.Abs(index) % ToolColors.Length];

    (double U, double V) P(double x, double y, double z) => _projection switch
    {
        NcProjection.Top => (x, y),
        NcProjection.Front => (x, z),
        NcProjection.Side => (y, z),
        _ => ((x - y) * 0.8660254, z + (x + y) * 0.5),
    };

    IEnumerable<NcSegment> All() => (_program?.Segments ?? new()).Concat(_other?.Segments ?? new());

    public void Fit()
    {
        if (_program == null || ActualWidth <= 0 || ActualHeight <= 0) { InvalidateVisual(); return; }
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var s in All())
        {
            foreach (var (u, v) in new[] { P(s.X1, s.Y1, s.Z1), P(s.X2, s.Y2, s.Z2) })
            { minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v); }
        }
        if (minU == double.MaxValue) { _zoom = 1; _panX = ActualWidth / 2; _panY = ActualHeight / 2; InvalidateVisual(); return; }
        var w = Math.Max(maxU - minU, 1e-3); var h = Math.Max(maxV - minV, 1e-3);
        _zoom = Math.Min((ActualWidth - 40) / w, (ActualHeight - 40) / h);
        _panX = ActualWidth / 2 - (minU + maxU) / 2 * _zoom;
        _panY = ActualHeight / 2 + (minV + maxV) / 2 * _zoom;
        InvalidateVisual();
    }

    Point S(double x, double y, double z) { var (u, v) = P(x, y, z); return new Point(_panX + u * _zoom, _panY - v * _zoom); }

    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); if (info.PreviousSize.Width == 0) Fit(); else InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (_program == null) return;
        DrawAxes(dc);
        if (_other != null)
        {
            // comparison: the other (old) program in red underneath, this (new) one in green on top
            Draw(dc, _other, int.MaxValue, _ => Color.FromRgb(214, 40, 40), 1.6);
            Draw(dc, _program, _upTo, _ => Color.FromRgb(16, 150, 64), 1.2);
        }
        else Draw(dc, _program, _upTo, s => _byTool ? ToolColors[Math.Abs(s.Tool) % ToolColors.Length] : s.Kind == MoveKind.Drill ? Color.FromRgb(16, 150, 64) : Color.FromRgb(15, 108, 189), 1.2);
        // selected program line on top
        if (_selectedLine >= 0)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 170, 0)), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze();
            foreach (var s in _program.Segments.Where(s => s.Line == _selectedLine)) dc.DrawLine(pen, S(s.X1, s.Y1, s.Z1), S(s.X2, s.Y2, s.Z2));
        }
        // tool position at the end of the simulation
        if (_upTo < _program.Segments.Count && _upTo >= 0)
        {
            var s = _program.Segments[Math.Min(_upTo, _program.Segments.Count - 1)];
            var p = S(s.X2, s.Y2, s.Z2);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 255, 170, 0)), new Pen(Brushes.Black, 1), p, 6, 6);
        }
    }

    void Draw(DrawingContext dc, NcProgram prog, int upTo, Func<NcSegment, Color> color, double width)
    {
        var groups = new Dictionary<(Color, bool), StreamGeometry>();
        var contexts = new Dictionary<(Color, bool), StreamGeometryContext>();
        var n = Math.Min(prog.Segments.Count, upTo == int.MaxValue ? int.MaxValue : upTo + 1);
        for (var i = 0; i < n; i++)
        {
            var s = prog.Segments[i];
            var rapid = s.Kind == MoveKind.Rapid;
            if (rapid && !_showRapids) continue;
            var key = (rapid ? Color.FromRgb(230, 120, 30) : color(s), rapid);
            if (!contexts.TryGetValue(key, out var ctx)) { var g = new StreamGeometry(); groups[key] = g; ctx = g.Open(); contexts[key] = ctx; }
            ctx.BeginFigure(S(s.X1, s.Y1, s.Z1), false, false);
            ctx.LineTo(S(s.X2, s.Y2, s.Z2), true, false);
        }
        foreach (var (key, ctx) in contexts) ctx.Close();
        foreach (var ((c, rapid), g) in groups)
        {
            g.Freeze();
            var pen = new Pen(new SolidColorBrush(c), rapid ? 0.9 : width) { DashStyle = rapid ? new DashStyle(new double[] { 4, 3 }, 0) : null };
            pen.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    void DrawAxes(DrawingContext dc)
    {
        var o = S(0, 0, 0); var len = 40 / Math.Max(_zoom, 1e-9);
        void Axis(double x, double y, double z, Color c, string name)
        {
            var e = S(x, y, z);
            var d = (e - o); if (d.Length < 1) return;
            d.Normalize(); var end = o + d * 36;
            dc.DrawLine(new Pen(new SolidColorBrush(c), 1.5), o, end);
            var ft = new FormattedText(name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, new SolidColorBrush(c), 1.0);
            dc.DrawText(ft, end + d * 4 - new Vector(ft.Width / 2, ft.Height / 2));
        }
        Axis(len, 0, 0, Color.FromRgb(214, 40, 40), "X"); Axis(0, len, 0, Color.FromRgb(16, 150, 64), "Y"); Axis(0, 0, len, Color.FromRgb(15, 108, 189), "Z");
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this); var f = e.Delta > 0 ? 1.2 : 1 / 1.2;
        _panX = p.X - (p.X - _panX) * f; _panY = p.Y - (p.Y - _panY) * f; _zoom *= f;
        InvalidateVisual(); e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); _drag = e.GetPosition(this); _dragPan = new Point(_panX, _panY); CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        _panX = _dragPan.X + p.X - d.X; _panY = _dragPan.Y + p.Y - d.Y; InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_drag is { } d && (e.GetPosition(this) - d).Length < 4 && _program != null)
        {
            // a click (not a drag): nearest segment within 8 px
            var p = e.GetPosition(this); var best = -1; var bestDist = 8.0;
            foreach (var s in _program.Segments)
            {
                if (s.Kind == MoveKind.Rapid && !_showRapids) continue;
                var dist = DistToSegment(p, S(s.X1, s.Y1, s.Z1), S(s.X2, s.Y2, s.Z2));
                if (dist < bestDist) { bestDist = dist; best = s.Line; }
            }
            if (best >= 0) { SelectedLine = best; LineClicked?.Invoke(best); }
        }
        _drag = null;
    }

    static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a; var len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        var t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}

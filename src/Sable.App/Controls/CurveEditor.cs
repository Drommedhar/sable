using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sable.Engine.Layers;

namespace Sable.App;

/// <summary>
/// Photoshop/Affinity-style curve editor for a <see cref="AdjustmentLayer"/> (Curves kind).
/// Drag control points to reshape the tone curve; click an empty spot to add a point;
/// right-click a point (non-endpoint) to delete it. Edits one channel at a time
/// (0=RGB composite, 1=R, 2=G, 3=B). Raises <see cref="Changed"/> on any user edit.
/// </summary>
public sealed class CurveEditor : Control
{
    private AdjustmentLayer? _adj;
    private int _channel;
    private int _dragIdx = -1;
    private int[]? _histogram;
    private const double Margin = 8;
    private const double HitPx = 9;

    public event Action? Changed;

    /// <summary>Backdrop histogram (int[768] R/G/B) drawn behind the grid; null hides it.</summary>
    public void SetHistogram(int[]? bins) { _histogram = bins; InvalidateVisual(); }

    public CurveEditor() { MinWidth = 220; MinHeight = 220; }

    protected override Size MeasureOverride(Size available)
    {
        double s = double.IsInfinity(available.Width) ? 240 : Math.Max(MinWidth, available.Width);
        return new Size(s, s);
    }

    /// <summary>Point at the adjustment + active channel (re-uses the same control across selections).</summary>
    public void SetTarget(AdjustmentLayer? adj, int channel)
    {
        _adj = adj; _channel = Math.Clamp(channel, 0, AdjustmentLayer.CurveChannels - 1);
        _dragIdx = -1;
        InvalidateVisual();
    }

    public int Channel
    {
        get => _channel;
        set { _channel = Math.Clamp(value, 0, AdjustmentLayer.CurveChannels - 1); _dragIdx = -1; InvalidateVisual(); }
    }

    private List<(float x, float y)>? Pts => _adj?.Curves[_channel];

    private Rect Plot => new(Margin, Margin, Math.Max(1, Bounds.Width - 2 * Margin), Math.Max(1, Bounds.Height - 2 * Margin));

    private Point ToPx((float x, float y) p)
        => new(Plot.X + p.x * Plot.Width, Plot.Y + (1 - p.y) * Plot.Height);

    private (float x, float y) ToCurve(Point px)
        => ((float)Math.Clamp((px.X - Plot.X) / Plot.Width, 0, 1),
            (float)Math.Clamp(1 - (px.Y - Plot.Y) / Plot.Height, 0, 1));

    // --- input ----------------------------------------------------------------
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pts = Pts; if (pts is null) return;
        var pos = e.GetPosition(this);
        int hit = FindPoint(pts, pos);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            if (hit > 0 && hit < pts.Count - 1) { pts.RemoveAt(hit); Edited(); }
            return;
        }

        if (hit >= 0) { _dragIdx = hit; }
        else
        {
            var c = ToCurve(pos);
            int i = 0; while (i < pts.Count && pts[i].x < c.x) i++;
            pts.Insert(i, c);
            _dragIdx = i;
            Edited();
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragIdx < 0) return;
        var pts = Pts; if (pts is null) return;
        var c = ToCurve(e.GetPosition(this));
        bool first = _dragIdx == 0, last = _dragIdx == pts.Count - 1;
        float x = c.x;
        if (first) x = 0f;
        else if (last) x = 1f;
        else
        {
            // keep strictly between neighbours so the list stays sorted
            float lo = pts[_dragIdx - 1].x + 1e-3f, hi = pts[_dragIdx + 1].x - 1e-3f;
            x = Math.Clamp(x, lo, hi);
        }
        pts[_dragIdx] = (x, c.y);
        Edited();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragIdx = -1;
        e.Pointer.Capture(null);
    }

    private int FindPoint(List<(float x, float y)> pts, Point pos)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            var px = ToPx(pts[i]);
            if (Math.Abs(px.X - pos.X) <= HitPx && Math.Abs(px.Y - pos.Y) <= HitPx) return i;
        }
        return -1;
    }

    private void Edited()
    {
        if (_adj is not null) _adj.Dirty = true;
        InvalidateVisual();
        Changed?.Invoke();
    }

    // --- render ---------------------------------------------------------------
    private IBrush ChannelBrush => _channel switch
    {
        1 => new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55)),
        2 => new SolidColorBrush(Color.FromRgb(0x55, 0xC0, 0x55)),
        3 => new SolidColorBrush(Color.FromRgb(0x55, 0x88, 0xE0)),
        _ => Brushes.White,
    };

    public override void Render(DrawingContext ctx)
    {
        var plot = Plot;
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), plot);

        // backdrop histogram (current channel, or all for the RGB composite tab)
        if (_histogram is { } h)
        {
            int mask = _channel switch { 1 => 1, 2 => 2, 3 => 4, _ => 7 };
            Histogram.Draw(ctx, plot, h, mask);
        }

        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1);
        for (int i = 1; i < 4; i++)
        {
            double gx = plot.X + plot.Width * i / 4.0, gy = plot.Y + plot.Height * i / 4.0;
            ctx.DrawLine(grid, new Point(gx, plot.Y), new Point(gx, plot.Bottom));
            ctx.DrawLine(grid, new Point(plot.X, gy), new Point(plot.Right, gy));
        }
        // identity diagonal
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)), 1) { DashStyle = DashStyle.Dash },
                     new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Y));
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1), plot);

        var pts = Pts; if (pts is null || _adj is null) return;

        // curve polyline (sample the same eval the GPU LUT uses)
        var brush = ChannelBrush;
        var pen = new Pen(brush, 1.5);
        const int N = 96;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            for (int i = 0; i <= N; i++)
            {
                float x = i / (float)N;
                float y = _adj.EvalChannel(_channel, x);
                var p = ToPx((x, y));
                if (i == 0) gc.BeginFigure(p, false); else gc.LineTo(p);
            }
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);

        // control points
        foreach (var p in pts)
        {
            var px = ToPx(p);
            ctx.DrawEllipse(brush, new Pen(Brushes.Black, 1), px, 4, 4);
        }
    }
}

using Sable.Core;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Compositing;
using Sable.Engine.Layers;
using Sable.Tools;

namespace Sable.Canvas;

/// <summary>
/// GPU-side brush gesture (improvement plan §2 "GPU tile-writeback stamping"): mirrors
/// <see cref="BrushTool"/>'s stroke maths (spacing, pressure taper, jitter, shape) on the
/// CPU per dab, but stamps every dab into the layer's live GPU buffer via
/// <c>brush.wgsl</c> — no CPU pixel writes and no re-upload per move. <see cref="Complete"/>
/// reads the result back into <c>layer.Pixels</c> and marks the touched tiles, so the
/// existing RasterState undo + atlas coherence flows are unchanged.
/// </summary>
internal sealed class GpuBrushSession : IStrokeSession
{
    private readonly GpuCompositor _comp;
    private readonly PixelLayer _layer;
    private readonly BrushTool _brush;
    private readonly Action _onDab;
    private readonly HashSet<(int, int)> _touched = new();
    private readonly Random _rng;
    private bool _done;

    public GpuBrushSession(GpuCompositor comp, PixelLayer layer, BrushTool brush, Document doc, Action onDab)
    {
        _comp = comp;
        _layer = layer;
        _brush = brush;
        _onDab = onDab;
        _rng = brush.JitterSeed != 0 ? new Random(brush.JitterSeed) : new Random();

        _comp.BeginBrushStroke(layer, brush.Tip, brush.TipW, brush.TipH, brush.ClipMask, doc.Width, doc.Height);
        var clip = brush.Clip;
        _comp.ConfigureBrushClip(brush.TipW, brush.TipH, brush.ClipMaskW, doc.Height,
            clip?.X ?? 0, clip?.Y ?? 0,
            clip is { } c ? c.X + c.W : 0, clip is { } c2 ? c2.Y + c2.H : 0);
    }

    /// <summary>Document-px segment → buffer-local dabs (mirror of BrushTool.Stroke spacing).</summary>
    public void StrokeTo(double x0, double y0, double x1, double y1, float p0 = 1f, float p1 = 1f)
    {
        if (_done) return;
        x0 -= _layer.OffsetX; y0 -= _layer.OffsetY;
        x1 -= _layer.OffsetX; y1 -= _layer.OffsetY;

        double dx = x1 - x0, dy = y1 - y0;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double spacing = _brush.Spacing > 0
            ? Math.Max(1.0, _brush.Spacing * 2.0 * _brush.Radius)
            : Math.Max(1.0, _brush.Radius * 0.25);
        int steps = (int)(dist / spacing);
        for (int s = 0; s <= steps; s++)
        {
            double f = steps == 0 ? 0 : (double)s / steps;
            float pressure = p0 + (p1 - p0) * (float)f;
            Stamp(x0 + dx * f, y0 + dy * f, pressure);
        }
        _onDab();
    }

    private void Stamp(double cx, double cy, float pressure)
    {
        var b = _brush;

        // pressure + per-dab jitter — identical maths to BrushTool.Stamp
        float r = b.Radius * (b.PressureSize ? 0.1f + 0.9f * Math.Clamp(pressure, 0f, 1f) : 1f);
        float ang = b.Angle;
        float flowJ = 1f;
        if (b.SizeJitter > 0f) r *= 1f - b.SizeJitter * (float)_rng.NextDouble();
        if (b.FlowJitter > 0f) flowJ = 1f - b.FlowJitter * (float)_rng.NextDouble();
        if (b.AngleJitter > 0f) ang += (float)(_rng.NextDouble() * 2 - 1) * 180f * b.AngleJitter;
        if (b.ScatterJitter > 0f)
        {
            cx += (_rng.NextDouble() * 2 - 1) * b.ScatterJitter * 2f * b.Radius;
            cy += (_rng.NextDouble() * 2 - 1) * b.ScatterJitter * 2f * b.Radius;
        }
        r = MathF.Max(r, 0.5f);

        bool hasTip = b.Tip is not null && b.TipW > 0 && b.TipH > 0;
        float radians = ang * MathF.PI / 180f;
        float thx = r, thy = r;
        if (hasTip)
        {
            float sc = 2f * r / Math.Max(b.TipW, b.TipH);
            thx = b.TipW * sc * 0.5f;
            thy = b.TipH * sc * 0.5f;
        }
        float reach = hasTip || ang != 0f ? MathF.Sqrt(thx * thx + thy * thy) : r;

        int x0 = Math.Max(0, (int)Math.Floor(cx - reach));
        int x1 = Math.Min(_layer.Width - 1, (int)Math.Ceiling(cx + reach));
        int y0 = Math.Max(0, (int)Math.Floor(cy - reach));
        int y1 = Math.Min(_layer.Height - 1, (int)Math.Ceiling(cy + reach));
        if (x1 < x0 || y1 < y0) return;

        uint flags = 0;
        if (b.Erase) flags |= GpuDabFlags.Erase;
        if (b.LockAlpha) flags |= GpuDabFlags.LockAlpha;
        if (b.Pencil) flags |= GpuDabFlags.Pencil;
        if (hasTip) flags |= GpuDabFlags.Tip;
        if (b.Clone) flags |= GpuDabFlags.Clone;
        if (b.Heal) flags |= GpuDabFlags.Heal;
        if (b.ClipMask is not null) flags |= GpuDabFlags.ClipMask;
        if (b.Clip is not null) flags |= GpuDabFlags.ClipRect;

        var dab = new GpuDab
        {
            Cx = (float)cx, Cy = (float)cy, R = r,
            Inner = r * Math.Clamp(b.Hardness, 0f, 0.99f),
            CosA = MathF.Cos(radians), SinA = MathF.Sin(radians),
            Round = MathF.Max(b.Roundness, 0.025f),
            Sa = b.Flow * flowJ * b.Alpha * (b.PressureFlow ? Math.Clamp(pressure, 0f, 1f) : 1f),
            ColR = b.R / 255f, ColG = b.G / 255f, ColB = b.B / 255f,
            Strength = b.Strength,
            Mode = (uint)b.Mode,
            Blend = (uint)b.PaintBlend,
            Flags = flags,
            CloneOffX = b.CloneOffX, CloneOffY = b.CloneOffY,
            Thx = thx, Thy = thy,
            Bx = x0, By = y0, Bw = x1 - x0 + 1, Bh = y1 - y0 + 1,
        };
        _comp.StampBrushDab(in dab);

        int ts = RasterTiles.TileSize;
        for (int ty = y0 / ts; ty <= y1 / ts; ty++)
        for (int tx = x0 / ts; tx <= x1 / ts; tx++)
            _touched.Add((tx, ty));
    }

    /// <summary>Read the stroked pixels back to the CPU and mark the touched tiles dirty
    /// (atlas re-sync). Must run before TrimToContent / undo capture.</summary>
    public void Complete()
    {
        if (_done) return;
        _done = true;
        _comp.EndBrushStroke();
        if (_touched.Count > 0) _layer.MarkTilesDirty(_touched);
    }

    /// <summary>Pixel-paint undo is the whole-raster RasterStateCommand (caller-owned) — nothing here.</summary>
    public IUndoableCommand? Finalize()
    {
        Complete();
        return null;
    }
}

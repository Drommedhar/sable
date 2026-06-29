using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Tools;

/// <summary>A brush gesture sink: CPU (<see cref="StrokeSession{T}"/>) or the GPU stroke
/// pipeline. Coordinates are document px; pressures are the segment-end stylus values.</summary>
public interface IStrokeSession
{
    void StrokeTo(double x0, double y0, double x1, double y1, float p0 = 1f, float p1 = 1f);
    IUndoableCommand? Finalize();
}

/// <summary>Factory helpers so the generic <see cref="StrokeSession{T}"/> infers its channel
/// type from the target buffer (C# can't infer generic type args on a constructor).</summary>
public static class StrokeSession
{
    /// <summary>Create a stroke session over a float[] RGBA32F (pixel layer) or byte[] RGBA8 (mask) target.</summary>
    public static StrokeSession<T> Create<T>(T[] target, int width, int height, BrushTool brush,
        Action<IReadOnlyCollection<(int, int)>> markTiles, int originX = 0, int originY = 0,
        Func<T[]?>? liveTarget = null) where T : struct
        => new(target, width, height, brush, markTiles, originX, originY, liveTarget);
}

/// <summary>
/// One brush gesture (press → moves → release) as a single undo unit, painting into any RGBA
/// target buffer — a layer's <b>float[] pixels</b> or its <b>byte[] mask</b>. Snapshots each
/// touched 256² tile copy-on-first-touch, then paints. <see cref="Finalize"/> captures the
/// after-state and produces a <see cref="PaintRasterCommand{T}"/>.
/// </summary>
public sealed class StrokeSession<T> : IStrokeSession where T : struct
{
    private readonly T[] _target;
    private readonly Func<T[]?> _live;   // live buffer fetch for the produced undo command
    private readonly int _w, _h;
    private readonly int _ox, _oy;   // document position of the target buffer's (0,0)
    private readonly BrushTool _brush;
    private readonly Action<IReadOnlyCollection<(int, int)>> _markTiles;
    private readonly Dictionary<(int tx, int ty), T[]> _before = new();

    public StrokeSession(T[] target, int width, int height, BrushTool brush,
        Action<IReadOnlyCollection<(int, int)>> markTiles, int originX = 0, int originY = 0,
        Func<T[]?>? liveTarget = null)
    {
        _target = target;
        _live = liveTarget ?? (() => target);
        _w = width;
        _h = height;
        _ox = originX;
        _oy = originY;
        _brush = brush;
        _markTiles = markTiles;
    }

    /// <summary>Snapshot the tiles this segment will touch, then paint it. Coords are document px.
    /// <paramref name="p0"/>/<paramref name="p1"/> = stylus pressure at the segment ends (1 = mouse).</summary>
    public void StrokeTo(double x0, double y0, double x1, double y1, float p0 = 1f, float p1 = 1f)
    {
        // to buffer-local space (the brush re-adds the origin for doc-space selection clipping)
        x0 -= _ox; y0 -= _oy; x1 -= _ox; y1 -= _oy;
        double r = _brush.MaxReach + 1;   // covers tip diagonal + scatter, not just the radius
        double minX = Math.Min(x0, x1) - r, maxX = Math.Max(x0, x1) + r;
        double minY = Math.Min(y0, y1) - r, maxY = Math.Max(y0, y1) + r;
        var tiles = TilesIn(minX, minY, maxX, maxY);
        foreach (var t in tiles)
            if (!_before.ContainsKey(t)) _before[t] = RasterTiles.GetTile(_target, _w, _h, t.tx, t.ty);
        _brush.Stroke(_target, _w, _h, x0, y0, x1, y1, p0, p1);
        _markTiles(tiles);
    }

    private List<(int tx, int ty)> TilesIn(double minX, double minY, double maxX, double maxY)
    {
        int ts = RasterTiles.TileSize;
        int tx0 = Math.Max(0, (int)Math.Floor(minX / ts));
        int ty0 = Math.Max(0, (int)Math.Floor(minY / ts));
        int tx1 = Math.Min(RasterTiles.TilesX(_w) - 1, (int)Math.Floor(maxX / ts));
        int ty1 = Math.Min(RasterTiles.TilesY(_h) - 1, (int)Math.Floor(maxY / ts));
        var list = new List<(int, int)>();
        for (int ty = ty0; ty <= ty1; ty++)
        for (int tx = tx0; tx <= tx1; tx++)
            list.Add((tx, ty));
        return list;
    }

    /// <summary>Produce the undo command for the whole gesture, or null if nothing painted.</summary>
    public IUndoableCommand? Finalize()
    {
        if (_before.Count == 0) return null;
        var after = new Dictionary<(int, int), T[]>(_before.Count);
        foreach (var key in _before.Keys)
            after[key] = RasterTiles.GetTile(_target, _w, _h, key.tx, key.ty);
        return new PaintRasterCommand<T>(_live, _w, _h, _before, after, _markTiles);
    }
}

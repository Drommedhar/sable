using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Tools;

/// <summary>
/// One brush gesture (press → moves → release) as a single undo unit, painting
/// into any RGBA8 target buffer (a layer's pixels or its mask). Snapshots each
/// touched 256² tile copy-on-first-touch, then paints. <see cref="Finalize"/>
/// captures the after-state and produces a <see cref="PaintRasterCommand"/>.
/// </summary>
public sealed class StrokeSession
{
    private readonly byte[] _target;
    private readonly int _w, _h;
    private readonly BrushTool _brush;
    private readonly Action<IReadOnlyCollection<(int, int)>> _markTiles;
    private readonly Dictionary<(int tx, int ty), byte[]> _before = new();

    public StrokeSession(byte[] target, int width, int height, BrushTool brush,
        Action<IReadOnlyCollection<(int, int)>> markTiles)
    {
        _target = target;
        _w = width;
        _h = height;
        _brush = brush;
        _markTiles = markTiles;
    }

    /// <summary>Snapshot the tiles this segment will touch, then paint it.</summary>
    public void StrokeTo(double x0, double y0, double x1, double y1)
    {
        double r = _brush.Radius + 1;
        double minX = Math.Min(x0, x1) - r, maxX = Math.Max(x0, x1) + r;
        double minY = Math.Min(y0, y1) - r, maxY = Math.Max(y0, y1) + r;
        var tiles = TilesIn(minX, minY, maxX, maxY);
        foreach (var t in tiles)
            if (!_before.ContainsKey(t)) _before[t] = RasterTiles.GetTile(_target, _w, _h, t.tx, t.ty);
        _brush.Stroke(_target, _w, _h, x0, y0, x1, y1);
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
        var after = new Dictionary<(int, int), byte[]>(_before.Count);
        foreach (var key in _before.Keys)
            after[key] = RasterTiles.GetTile(_target, _w, _h, key.tx, key.ty);
        return new PaintRasterCommand(_target, _w, _h, _before, after, _markTiles);
    }
}

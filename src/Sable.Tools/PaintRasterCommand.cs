using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Tools;

/// <summary>
/// Undoable destructive edit of an RGBA8 raster (layer pixels or a mask), captured
/// as dirty-tile snapshots (PLAN §4/§5B): before/after bytes of every 256² tile a
/// stroke touched. Undo restores 'before', redo restores 'after'. Memory bounded
/// to touched tiles.
/// </summary>
public sealed class PaintRasterCommand : IUndoableCommand
{
    private readonly byte[] _target;
    private readonly int _w, _h;
    private readonly Dictionary<(int tx, int ty), byte[]> _before;
    private readonly Dictionary<(int tx, int ty), byte[]> _after;
    private readonly Action<IReadOnlyCollection<(int, int)>> _markTiles;

    public PaintRasterCommand(byte[] target, int width, int height,
        Dictionary<(int, int), byte[]> before,
        Dictionary<(int, int), byte[]> after,
        Action<IReadOnlyCollection<(int, int)>> markTiles)
    {
        _target = target;
        _w = width;
        _h = height;
        _before = before;
        _after = after;
        _markTiles = markTiles;
    }

    public string Name => "Paint";

    public void Do() => Apply(_after);
    public void Undo() => Apply(_before);

    private void Apply(Dictionary<(int tx, int ty), byte[]> tiles)
    {
        foreach (var ((tx, ty), bytes) in tiles)
            RasterTiles.SetTile(_target, _w, _h, tx, ty, bytes);
        _markTiles(tiles.Keys);
    }
}

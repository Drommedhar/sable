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
    // Live accessor, NOT a captured array ref: a later edit can swap the layer's
    // buffer (SetBuffer / ExpandToCover), orphaning a captured ref so undo silently
    // no-ops. Re-reading at apply time always targets the current buffer.
    private readonly Func<byte[]?> _target;
    private readonly int _w, _h;
    private readonly Dictionary<(int tx, int ty), byte[]> _before;
    private readonly Dictionary<(int tx, int ty), byte[]> _after;
    private readonly Action<IReadOnlyCollection<(int, int)>> _markTiles;

    public PaintRasterCommand(Func<byte[]?> target, int width, int height,
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

    /// <summary>Back-compat overload for a stable buffer (tests / non-resizing targets).</summary>
    public PaintRasterCommand(byte[] target, int width, int height,
        Dictionary<(int, int), byte[]> before,
        Dictionary<(int, int), byte[]> after,
        Action<IReadOnlyCollection<(int, int)>> markTiles)
        : this(() => target, width, height, before, after, markTiles) { }

    public string Name => "Paint";

    public void Do() => Apply(_after);
    public void Undo() => Apply(_before);

    private void Apply(Dictionary<(int tx, int ty), byte[]> tiles)
    {
        var target = _target();
        // Geometry changed under us (layer resized): the captured tile coords no
        // longer map → skip rather than corrupt the buffer.
        if (target is null || target.Length != _w * _h * 4) return;
        foreach (var ((tx, ty), bytes) in tiles)
            RasterTiles.SetTile(target, _w, _h, tx, ty, bytes);
        _markTiles(tiles.Keys);
    }
}

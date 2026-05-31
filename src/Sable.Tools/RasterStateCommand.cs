using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Tools;

/// <summary>
/// Snapshot of a pixel layer's whole raster state (buffer + size + document offset). Used by
/// <see cref="RasterStateCommand"/> when a paint gesture resizes the layer (grow-to-paint +
/// auto-crop-to-content), where tile-diff undo can't work because the buffer geometry changes.
/// </summary>
public readonly struct RasterState
{
    public readonly byte[] Pixels;
    public readonly int Width, Height, OffsetX, OffsetY;

    private RasterState(byte[] pixels, int w, int h, int ox, int oy)
    { Pixels = pixels; Width = w; Height = h; OffsetX = ox; OffsetY = oy; }

    /// <summary>Capture a (deep-copied) snapshot of the layer's current raster state.</summary>
    public static RasterState Capture(PixelLayer layer)
        => new((byte[])layer.Pixels.Clone(), layer.Width, layer.Height, layer.OffsetX, layer.OffsetY);

    /// <summary>Restore this state into the layer (fresh buffer copy so the snapshot stays immutable).</summary>
    public void ApplyTo(PixelLayer layer)
    {
        layer.SetBuffer(Width, Height, (byte[])Pixels.Clone());
        layer.OffsetX = OffsetX;
        layer.OffsetY = OffsetY;
    }
}

/// <summary>
/// Undoable whole-layer raster edit: swaps the layer between two <see cref="RasterState"/>s.
/// Used for paint gestures that change the layer's bounds (dynamic layer bounds, PLAN §1.1).
/// Memory is bounded to the content-sized before/after buffers, not the document.
/// </summary>
public sealed class RasterStateCommand : IUndoableCommand
{
    private readonly PixelLayer _layer;
    private readonly RasterState _before, _after;
    private readonly System.Action _markDirty;

    public RasterStateCommand(PixelLayer layer, RasterState before, RasterState after, System.Action markDirty)
    {
        _layer = layer;
        _before = before;
        _after = after;
        _markDirty = markDirty;
    }

    public string Name => "Paint";

    public void Do() { _after.ApplyTo(_layer); _markDirty(); }
    public void Undo() { _before.ApplyTo(_layer); _markDirty(); }
}

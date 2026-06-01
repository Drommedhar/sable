using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Engine.Commands;

/// <summary>
/// Crop the document to a rectangle (doc px): resizes the canvas and rebuilds every
/// pixel layer + mask to the cropped region. Layer offsets/transforms are preserved
/// (the crop just shifts the buffer origin). Undo restores the original dimensions
/// and buffers. Heavy snapshot, but crop is a rare structural op.
/// </summary>
public sealed class CropCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly int _x, _y, _w, _h;
    private int _oldW, _oldH;
    private readonly List<(PixelLayer layer, byte[] px, int w, int h)> _pixSnap = new();
    private readonly List<(Layer layer, byte[]? mask)> _maskSnap = new();
    private bool _captured;

    public CropCommand(Document doc, int x, int y, int w, int h)
    {
        _doc = doc; _x = x; _y = y; _w = Math.Max(1, w); _h = Math.Max(1, h);
    }

    public string Name => "Crop";

    public void Do()
    {
        if (!_captured) { _oldW = _doc.Width; _oldH = _doc.Height; CaptureList(_doc.Layers); _captured = true; }
        ApplyCrop(_doc.Layers, _oldW, _oldH);   // crop buffers (still at original size) to the region
        _doc.SetSize(_w, _h);
        _doc.ClearSelection();
    }

    private void CaptureList(List<Layer> list)
    {
        foreach (var l in list)
        {
            if (l is PixelLayer px) _pixSnap.Add((px, px.Pixels, px.Width, px.Height));
            _maskSnap.Add((l, l.Mask));
            if (l is GroupLayer g) CaptureList(g.Children);
        }
    }

    private void ApplyCrop(List<Layer> list, int srcW, int srcH)
    {
        foreach (var l in list)
        {
            if (l is PixelLayer px)
                px.SetBuffer(_w, _h, RasterTiles.Crop(px.Pixels, px.Width, px.Height, _x, _y, _w, _h));
            if (l.Mask is { } m)
            {
                l.Mask = RasterTiles.Crop(m, srcW, srcH, _x, _y, _w, _h);
                l.MaskDirty = true; l.Dirty = true;
            }
            if (l is GroupLayer g) ApplyCrop(g.Children, srcW, srcH);
        }
    }

    public void Undo()
    {
        _doc.SetSize(_oldW, _oldH);
        foreach (var (layer, px, w, h) in _pixSnap) layer.SetBuffer(w, h, px);
        foreach (var (layer, mask) in _maskSnap) { layer.Mask = mask; layer.MaskDirty = true; layer.Dirty = true; }
        _doc.ClearSelection();
    }
}

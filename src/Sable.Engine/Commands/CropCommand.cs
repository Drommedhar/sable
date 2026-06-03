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
    private readonly List<(PixelLayer layer, byte[] px, int w, int h, int offX, int offY)> _pixSnap = new();
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
            if (l is PixelLayer px) _pixSnap.Add((px, px.Pixels, px.Width, px.Height, px.OffsetX, px.OffsetY));
            _maskSnap.Add((l, l.Mask));
            CaptureList(l.Children);
        }
    }

    private void ApplyCrop(List<Layer> list, int srcW, int srcH)
    {
        foreach (var l in list)
        {
            if (l is PixelLayer px)
            {
                // crop the layer buffer in LAYER-local space (doc crop origin minus the layer's offset),
                // then re-origin: the cropped region now starts at the new doc (0,0). Negative coords pad
                // transparent (= canvas grow). Mask is layer-aligned → crop it the same way.
                int olw = px.Width, olh = px.Height, oox = px.OffsetX, ooy = px.OffsetY;
                px.SetBuffer(_w, _h, RasterTiles.Crop(px.Pixels, olw, olh, _x - oox, _y - ooy, _w, _h));
                px.OffsetX = 0; px.OffsetY = 0;
                if (px.Mask is { } pm)
                {
                    px.Mask = RasterTiles.Crop(pm, olw, olh, _x - oox, _y - ooy, _w, _h);
                    px.MaskDirty = true; px.Dirty = true;
                }
            }
            else if (l.Mask is { } m)   // non-pixel layers carry document-sized masks
            {
                l.Mask = RasterTiles.Crop(m, srcW, srcH, _x, _y, _w, _h);
                l.MaskDirty = true; l.Dirty = true;
            }
            ApplyCrop(l.Children, srcW, srcH);   // group content / nested effect layers
        }
    }

    public void Undo()
    {
        _doc.SetSize(_oldW, _oldH);
        foreach (var (layer, px, w, h, offX, offY) in _pixSnap) { layer.SetBuffer(w, h, px); layer.OffsetX = offX; layer.OffsetY = offY; }
        foreach (var (layer, mask) in _maskSnap) { layer.Mask = mask; layer.MaskDirty = true; layer.Dirty = true; }
        _doc.ClearSelection();
    }
}

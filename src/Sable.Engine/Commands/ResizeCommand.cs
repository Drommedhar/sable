using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Engine.Commands;

/// <summary>
/// Resample the whole document to a new pixel size (Resize Document): scales every
/// pixel layer + mask (bilinear or nearest) and proportionally scales layer offsets.
/// DPI metadata updates too. Undo restores the original dimensions, buffers, and DPI.
/// </summary>
public sealed class ResizeCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly int _w, _h;
    private readonly double _dpi;
    private readonly bool _bilinear;
    private int _oldW, _oldH;
    private double _oldDpi;
    private readonly List<(PixelLayer layer, byte[] px, int w, int h, int offX, int offY)> _pixSnap = new();
    private readonly List<(Layer layer, byte[]? mask)> _maskSnap = new();
    private bool _captured;

    public ResizeCommand(Document doc, int newW, int newH, double dpi, bool bilinear)
    {
        _doc = doc; _w = Math.Max(1, newW); _h = Math.Max(1, newH); _dpi = dpi; _bilinear = bilinear;
    }

    public string Name => "Resize Document";

    public void Do()
    {
        if (!_captured) { _oldW = _doc.Width; _oldH = _doc.Height; _oldDpi = _doc.Dpi; CaptureList(_doc.Layers); _captured = true; }
        double rx = (double)_w / _oldW, ry = (double)_h / _oldH;
        ApplyList(_doc.Layers, _oldW, _oldH, rx, ry);
        _doc.SetSize(_w, _h);
        _doc.Dpi = _dpi;
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

    private void ApplyList(List<Layer> list, int srcW, int srcH, double rx, double ry)
    {
        foreach (var l in list)
        {
            if (l is PixelLayer px)
            {
                // scale the layer to ITS OWN new size (not the document's), and its offset proportionally —
                // a sub-doc/offset layer must keep its bounds, not stretch to fill the canvas.
                int olw = px.Width, olh = px.Height;
                int nlw = Math.Max(1, (int)Math.Round(olw * rx)), nlh = Math.Max(1, (int)Math.Round(olh * ry));
                px.SetBuffer(nlw, nlh, RasterTiles.Resample(px.Pixels, olw, olh, nlw, nlh, _bilinear));
                px.OffsetX = (int)Math.Round(px.OffsetX * rx);
                px.OffsetY = (int)Math.Round(px.OffsetY * ry);
                if (px.Mask is { } pm)   // mask is layer-aligned → scale to the new layer size
                {
                    px.Mask = RasterTiles.Resample(pm, olw, olh, nlw, nlh, _bilinear);
                    px.MaskDirty = true; px.Dirty = true;
                }
            }
            else if (l.Mask is { } m)   // non-pixel masks are document-sized
            {
                l.Mask = RasterTiles.Resample(m, srcW, srcH, _w, _h, _bilinear);
                l.MaskDirty = true; l.Dirty = true;
            }
            ApplyList(l.Children, srcW, srcH, rx, ry);   // group content / nested effect layers
        }
    }

    public void Undo()
    {
        _doc.SetSize(_oldW, _oldH);
        _doc.Dpi = _oldDpi;
        foreach (var (layer, px, w, h, offX, offY) in _pixSnap) { layer.SetBuffer(w, h, px); layer.OffsetX = offX; layer.OffsetY = offY; }
        foreach (var (layer, mask) in _maskSnap) { layer.Mask = mask; layer.MaskDirty = true; layer.Dirty = true; }
        _doc.ClearSelection();
    }
}

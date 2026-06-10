using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Engine.Commands;

/// <summary>
/// Non-destructive document crop: resizes the canvas to the rectangle and shifts every
/// content layer's offset so the view stays put, but KEEPS all pixels — layers already
/// support independent bounds, so anything outside the new canvas is preserved (un-crop
/// by enlarging the canvas or moving layers later). Groups are not shifted (their children
/// are placed in document space); doc-sized masks on non-pixel layers are cropped with a
/// snapshot for undo (the engine requires them canvas-aligned).
/// </summary>
public sealed class CropKeepCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly int _x, _y, _w, _h;
    private int _oldW, _oldH;
    private readonly List<(Layer layer, byte[]? mask)> _maskSnap = new();
    private bool _captured;

    public CropKeepCommand(Document doc, int x, int y, int w, int h)
    {
        _doc = doc; _x = x; _y = y; _w = Math.Max(1, w); _h = Math.Max(1, h);
    }

    public string Name => "Crop";

    public void Do()
    {
        if (!_captured)
        {
            _oldW = _doc.Width; _oldH = _doc.Height;
            CaptureMasks(_doc.Layers);
            _captured = true;
        }
        Shift(_doc.Layers, -_x, -_y);
        CropDocMasks(_doc.Layers, _oldW, _oldH);
        _doc.SetSize(_w, _h);
        _doc.ClearSelection();
    }

    public void Undo()
    {
        Shift(_doc.Layers, _x, _y);
        foreach (var (layer, mask) in _maskSnap)
        {
            layer.Mask = mask;
            layer.MaskDirty = true; layer.Dirty = true;
        }
        _doc.SetSize(_oldW, _oldH);
        _doc.ClearSelection();
    }

    private void CaptureMasks(List<Layer> list)
    {
        foreach (var l in list)
        {
            if (l is not PixelLayer && l.Mask is not null) _maskSnap.Add((l, l.Mask));
            CaptureMasks(l.Children);
        }
    }

    /// <summary>Shift content layers (not groups — their children are placed in doc space).</summary>
    private static void Shift(List<Layer> list, int dx, int dy)
    {
        foreach (var l in list)
        {
            if (l is not GroupLayer)
            {
                l.OffsetX += dx;
                l.OffsetY += dy;
                l.Dirty = true;
            }
            Shift(l.Children, dx, dy);
        }
    }

    /// <summary>Non-pixel layers carry document-sized masks — re-crop those to the new canvas.</summary>
    private void CropDocMasks(List<Layer> list, int srcW, int srcH)
    {
        foreach (var l in list)
        {
            if (l is not PixelLayer && l.Mask is { } m)
            {
                l.Mask = RasterTiles.Crop(m, srcW, srcH, _x, _y, _w, _h);
                l.MaskDirty = true; l.Dirty = true;
            }
            CropDocMasks(l.Children, srcW, srcH);
        }
    }
}

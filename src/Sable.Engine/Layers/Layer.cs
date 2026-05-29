using Sable.Core;

namespace Sable.Engine.Layers;

/// <summary>
/// Base of every node in the layer tree (PLAN §4). Pixel layers, groups, and —
/// later — adjustment / live-filter layers all derive from this. Effects are
/// first-class layers, so adjustments/filters will be Layer subclasses too.
/// </summary>
public abstract class Layer
{
    public string Name { get; set; } = "Layer";

    /// <summary>0..1 layer opacity.</summary>
    public float Opacity { get; set; } = 1f;

    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    public bool Visible { get; set; } = true;

    /// <summary>Clip to the layer(s) below (clipping mask): only show where the backdrop is opaque (PLAN §5A.5).</summary>
    public bool ClipToBelow { get; set; }

    /// <summary>Non-destructive position offset in document pixels (Move tool).</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    /// <summary>Non-destructive scale (1 = none) and rotation (degrees) about the layer centre (Transform tool).</summary>
    public float ScaleX { get; set; } = 1f;
    public float ScaleY { get; set; } = 1f;
    public float Rotation { get; set; }

    public bool HasTransform => OffsetX != 0 || OffsetY != 0 || ScaleX != 1f || ScaleY != 1f || Rotation != 0f;

    /// <summary>Tight content bounds in doc px (before offset). Default = whole document; shape layers override.</summary>
    public virtual (int x, int y, int w, int h) ContentBounds(int docW, int docH) => (0, 0, docW, docH);

    /// <summary>Set when GPU-side data must be (re)uploaded/recomposited.</summary>
    public bool Dirty { get; set; } = true;

    /// <summary>
    /// Tiles changed since the last GPU upload (PLAN §4 partial upload). Empty +
    /// Dirty = upload the whole layer (first time / bulk change). The compositor
    /// uploads only these tiles when non-empty, then clears them.
    /// </summary>
    public HashSet<(int tx, int ty)> DirtyTiles { get; } = new();

    /// <summary>Mark specific tiles changed (and flag for recomposite).</summary>
    public void MarkTilesDirty(IEnumerable<(int, int)> tiles)
    {
        foreach (var t in tiles) DirtyTiles.Add(t);
        Dirty = true;
    }

    /// <summary>
    /// Optional per-layer raster mask (PLAN §4): RGBA8, R channel = coverage 0..255.
    /// Multiplies the layer's contribution (pixel alpha / adjustment strength).
    /// Null = no mask (full coverage). Same dimensions as the document.
    /// </summary>
    public byte[]? Mask { get; set; }

    public bool MaskDirty { get; set; }

    public bool HasMask => Mask is not null;

    /// <summary>Attach a white (fully-revealing) mask sized to the document.</summary>
    public void AddWhiteMask(int width, int height)
    {
        var m = new byte[width * height * 4];
        Array.Fill(m, (byte)255);
        Mask = m;
        MaskDirty = true;
        Dirty = true;
    }

    public void RemoveMask()
    {
        Mask = null;
        MaskDirty = true;
        Dirty = true;
    }
}

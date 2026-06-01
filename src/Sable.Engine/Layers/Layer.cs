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

    /// <summary>0..1 layer opacity (scales the whole layer incl. future FX).</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>0..1 fill opacity (scales the layer's pixels only, not FX — PS "Fill"). Multiplies with Opacity for now.</summary>
    public float FillOpacity { get; set; } = 1f;

    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    public bool Visible { get; set; } = true;

    /// <summary>Clip to the layer(s) below (clipping mask): only show where the backdrop is opaque (PLAN §5A.5).</summary>
    public bool ClipToBelow { get; set; }

    /// <summary>Lock position/transform (Move blocked), lock pixels (paint blocked), lock alpha (paint preserves alpha). PLAN §16.3.</summary>
    public bool LockPosition { get; set; }
    public bool LockPixels { get; set; }
    public bool LockAlpha { get; set; }

    /// <summary>Colour tag index 0=none, 1..7 = red/orange/yellow/green/blue/purple/grey (Affinity row strip).</summary>
    public int ColorTag { get; set; }

    /// <summary>Non-destructive position offset in document pixels (Move tool).</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    /// <summary>Non-destructive scale (1 = none) and rotation (degrees) about the layer centre (Transform tool).</summary>
    public float ScaleX { get; set; } = 1f;
    public float ScaleY { get; set; } = 1f;
    public float Rotation { get; set; }

    /// <summary>Non-destructive shear (0 = none): X = horizontal slant, Y = vertical slant.</summary>
    public float ShearX { get; set; }
    public float ShearY { get; set; }

    /// <summary>Perspective/distort: when true, the layer's 4 corners are free-dragged to
    /// <see cref="PerspCorners"/> (doc px, TL,TR,BR,BL = 8 floats) and the affine is ignored.</summary>
    public bool Perspective { get; set; }
    public float[]? PerspCorners { get; set; }

    public bool HasTransform => OffsetX != 0 || OffsetY != 0 || ScaleX != 1f || ScaleY != 1f || Rotation != 0f || ShearX != 0f || ShearY != 0f;

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

    /// <summary>Non-destructive layer effects (drop shadow, glow, stroke, overlay) — PLAN §5/§16.6.</summary>
    public List<LayerEffect> Effects { get; } = new();

    public bool HasEffects => Effects.Count > 0;

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

    /// <summary>Deep copy (pixels/params/mask/effects/children) — for Duplicate + clipboard.</summary>
    public Layer Clone()
    {
        var c = CreateClone();
        c.Name = Name;
        c.Opacity = Opacity;
        c.FillOpacity = FillOpacity;
        c.BlendMode = BlendMode;
        c.Visible = Visible;
        c.ClipToBelow = ClipToBelow;
        c.LockPosition = LockPosition; c.LockPixels = LockPixels; c.LockAlpha = LockAlpha;
        c.ColorTag = ColorTag;
        c.OffsetX = OffsetX; c.OffsetY = OffsetY;
        c.ScaleX = ScaleX; c.ScaleY = ScaleY; c.Rotation = Rotation;
        c.ShearX = ShearX; c.ShearY = ShearY;
        c.Perspective = Perspective;
        if (PerspCorners is not null) c.PerspCorners = (float[])PerspCorners.Clone();
        if (Mask is not null) { c.Mask = (byte[])Mask.Clone(); c.MaskDirty = true; }
        foreach (var fx in Effects) c.Effects.Add(fx.Clone());
        c.Dirty = true;
        return c;
    }

    /// <summary>Create a typed copy with type-specific data (pixels/params/children); base props copied by <see cref="Clone"/>.</summary>
    protected abstract Layer CreateClone();
}

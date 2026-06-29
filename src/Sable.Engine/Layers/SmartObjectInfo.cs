namespace Sable.Engine.Layers;

/// <summary>
/// Captured metadata for a Photoshop Smart Object that was imported as a rasterised layer
/// (Smart Object staged import, roadmap §14 Tier 1 — see plans/SMART_OBJECTS.md). The placed
/// content is still rendered as raster pixels on the owning layer; this preserves the placement
/// transform + source identity so re-edit / round-trip stays possible (Tier 2: embedded-source
/// container). Null on a normal layer.
/// </summary>
public sealed class SmartObjectInfo
{
    /// <summary>Unique id (PSD <c>Idnt</c>) linking to the embedded/linked source in the lnk2 block.</summary>
    public string Identity { get; set; } = "";

    /// <summary>Placement transform quad — 8 floats = 4 corner points (x0,y0,…,x3,y3) the object was
    /// placed through (PSD <c>Trnf</c>). Null when not present.</summary>
    public float[]? Placement { get; set; }

    /// <summary>Embedded source's native pixel size (PSD <c>Sz</c>), 0 when unknown.</summary>
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    /// <summary>PSD <c>Type</c> (1 = image, 2 = raster PSD, …); 0 when unknown.</summary>
    public int SourceType { get; set; }

    /// <summary>True for a linked (external file) Smart Object vs an embedded one.</summary>
    public bool Linked { get; set; }

    public SmartObjectInfo Clone() => new()
    {
        Identity = Identity,
        Placement = Placement is null ? null : (float[])Placement.Clone(),
        SourceWidth = SourceWidth,
        SourceHeight = SourceHeight,
        SourceType = SourceType,
        Linked = Linked,
    };
}

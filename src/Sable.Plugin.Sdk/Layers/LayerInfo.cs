namespace Sable.Plugin.Sdk.Layers;

/// <summary>
/// Read-only snapshot of one layer (capability <c>layer.read</c>). Decoupled from the
/// engine's <c>Layer</c> tree. <see cref="Id"/> is a host-assigned stable handle used by
/// <see cref="ILayerWriteApi"/> — opaque to the plugin, valid for the session.
/// </summary>
public sealed record LayerInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>"pixel" | "adjustment" | "filter" | "group" | "shape" | "text" | "path".</summary>
    public required string Kind { get; init; }

    public required float Opacity { get; init; }       // 0..1
    public required float FillOpacity { get; init; }   // 0..1
    public required SdkBlendMode Blend { get; init; }
    public required bool Visible { get; init; }

    public bool Clipped { get; init; }
    public bool LockPosition { get; init; }
    public bool LockPixels { get; init; }
    public bool LockAlpha { get; init; }
    public int ColorTag { get; init; }

    public int OffsetX { get; init; }
    public int OffsetY { get; init; }

    public bool HasMask { get; init; }
    public bool HasEffects { get; init; }

    /// <summary>Parent group/layer id, or null when at document root.</summary>
    public string? ParentId { get; init; }

    public IReadOnlyList<string> ChildIds { get; init; } = Array.Empty<string>();

    /// <summary>Tight content bounds in doc px.</summary>
    public int BoundsX { get; init; }
    public int BoundsY { get; init; }
    public int BoundsWidth { get; init; }
    public int BoundsHeight { get; init; }
}

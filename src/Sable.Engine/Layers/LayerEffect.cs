using Sable.Core;

namespace Sable.Engine.Layers;

public enum LayerEffectKind
{
    DropShadow = 0,
    OuterGlow = 1,
    Stroke = 2,
    ColorOverlay = 3,
    InnerShadow = 4,
    InnerGlow = 5,
    GradientOverlay = 6,
    Bevel = 7,
}

public enum StrokePosition { Outside = 0, Inside = 1, Center = 2 }

/// <summary>
/// One non-destructive layer effect (PLAN §5/§16.6). Lives in <see cref="Layer.Effects"/>
/// and is rendered around the layer by the compositor: shadow/glow behind, overlay/stroke
/// on top. Colour is straight 0..1 RGB; <see cref="Opacity"/> + <see cref="BlendMode"/>
/// control how the effect sprite composites.
/// </summary>
public sealed class LayerEffect
{
    public LayerEffectKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float Opacity { get; set; } = 1f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    public float Radius { get; set; } = 6f;     // blur radius (shadow / glow); also inner-shadow/glow spread
    public float OffsetX { get; set; }           // drop/inner-shadow offset (doc px)
    public float OffsetY { get; set; }
    public float Size { get; set; } = 3f;        // stroke width (px)
    public StrokePosition StrokePos { get; set; } = StrokePosition.Outside;

    // Gradient Overlay: end colour + angle (start colour = R/G/B).
    // Bevel: R/G/B = highlight colour, R2/G2/B2 = shadow colour, Angle = light direction, Depth = strength, Size = width.
    public float R2 { get; set; } = 1f;
    public float G2 { get; set; } = 1f;
    public float B2 { get; set; } = 1f;
    public float Angle { get; set; }             // degrees
    public float Depth { get; set; } = 1f;       // bevel strength

    /// <summary>Create an effect with sensible per-kind defaults (colour/blend/params).</summary>
    public static LayerEffect Create(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.DropShadow => new LayerEffect
        { Kind = kind, R = 0, G = 0, B = 0, Opacity = 0.6f, BlendMode = BlendMode.Multiply, Radius = 6, OffsetX = 4, OffsetY = 4 },
        LayerEffectKind.OuterGlow => new LayerEffect
        { Kind = kind, R = 1, G = 0.9f, B = 0.5f, Opacity = 0.7f, BlendMode = BlendMode.Screen, Radius = 8 },
        LayerEffectKind.Stroke => new LayerEffect
        { Kind = kind, R = 0, G = 0, B = 0, Opacity = 1f, BlendMode = BlendMode.Normal, Size = 3, StrokePos = StrokePosition.Outside },
        LayerEffectKind.InnerShadow => new LayerEffect
        { Kind = kind, R = 0, G = 0, B = 0, Opacity = 0.6f, BlendMode = BlendMode.Multiply, Radius = 6, OffsetX = 4, OffsetY = 4 },
        LayerEffectKind.InnerGlow => new LayerEffect
        { Kind = kind, R = 1, G = 0.95f, B = 0.7f, Opacity = 0.7f, BlendMode = BlendMode.Screen, Radius = 6 },
        LayerEffectKind.GradientOverlay => new LayerEffect
        { Kind = kind, R = 0, G = 0, B = 0, R2 = 1, G2 = 1, B2 = 1, Opacity = 1f, BlendMode = BlendMode.Normal, Angle = 90 },
        LayerEffectKind.Bevel => new LayerEffect
        { Kind = kind, R = 1, G = 1, B = 1, R2 = 0, G2 = 0, B2 = 0, Opacity = 0.75f, BlendMode = BlendMode.Normal, Size = 4, Angle = 135, Depth = 1 },
        _ => new LayerEffect
        { Kind = LayerEffectKind.ColorOverlay, R = 1, G = 0, B = 0, Opacity = 1f, BlendMode = BlendMode.Normal },
    };

    public LayerEffect Clone() => (LayerEffect)MemberwiseClone();
}

namespace Sable.Engine.Layers;

/// <summary>
/// A non-destructive adjustment as a first-class tree node (PLAN §4/§5A.1): no
/// pixels of its own — the compositor applies its parametric transform (adjust.wgsl)
/// to the accumulated backdrop below it. Layer opacity = strength. <see cref="PackParams"/>
/// feeds the shader's generic p0..p5 per <see cref="Kind"/>.
/// </summary>
public sealed class AdjustmentLayer : Layer
{
    public AdjustmentKind Kind { get; }

    // Brightness/Contrast
    public float Brightness { get; set; }       // -1..1
    public float Contrast { get; set; } = 1f;   // 0..2

    // Levels
    public float InBlack { get; set; }          // 0..1
    public float InWhite { get; set; } = 1f;    // 0..1
    public float Gamma { get; set; } = 1f;      // >0

    // HSL
    public float HueShift { get; set; }         // turns (-0.5..0.5)
    public float Saturation { get; set; } = 1f; // 0..2
    public float Lightness { get; set; }        // -1..1

    public AdjustmentLayer(AdjustmentKind kind = AdjustmentKind.BrightnessContrast)
    {
        Kind = kind;
        Name = kind switch
        {
            AdjustmentKind.BrightnessContrast => "Brightness/Contrast",
            AdjustmentKind.Levels => "Levels",
            AdjustmentKind.Hsl => "HSL",
            _ => "Adjustment"
        };
    }

    /// <summary>Fill p0..p5 (6 floats) for the shader based on Kind.</summary>
    public void PackParams(Span<float> p)
    {
        p.Clear();
        switch (Kind)
        {
            case AdjustmentKind.Levels:
                p[0] = InBlack; p[1] = InWhite; p[2] = Gamma;
                break;
            case AdjustmentKind.Hsl:
                p[0] = HueShift; p[1] = Saturation; p[2] = Lightness;
                break;
            default: // BrightnessContrast
                p[0] = Brightness; p[1] = Contrast;
                break;
        }
    }
}

public enum AdjustmentKind
{
    BrightnessContrast = 0,
    Levels = 1,
    Hsl = 2,
}

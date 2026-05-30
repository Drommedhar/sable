namespace Sable.Core;

/// <summary>
/// Layer blend modes (PLAN §5A.4, full PS set + Affinity extras). Integer values are
/// the contract with the WGSL compositor (`composite.wgsl` `blend()` switches on these) —
/// keep in sync. Component modes (Hue/Saturation/Colour/Luminosity) + Darker/LighterColour
/// are non-separable (W3C helpers).
/// </summary>
public enum BlendMode
{
    Normal = 0,
    Multiply = 1,
    Screen = 2,
    Overlay = 3,
    Darken = 4,
    Lighten = 5,
    Add = 6,            // Linear Dodge
    ColorBurn = 7,
    LinearBurn = 8,
    DarkerColor = 9,
    ColorDodge = 10,
    LighterColor = 11,
    SoftLight = 12,
    HardLight = 13,
    VividLight = 14,
    LinearLight = 15,
    PinLight = 16,
    HardMix = 17,
    Difference = 18,
    Exclusion = 19,
    Subtract = 20,
    Divide = 21,
    Hue = 22,
    Saturation = 23,
    Color = 24,
    Luminosity = 25,
    Average = 26,       // Affinity extra
    Negation = 27,      // Affinity extra
    Reflect = 28,       // Affinity extra
    Glow = 29,          // Affinity extra
}

namespace Sable.Plugin.Sdk;

/// <summary>
/// Blend modes exposed to plugins. Integer values mirror the engine's blend-mode contract
/// 1:1 so the host adapts with an identity cast — but the enum is SDK-owned so plugins never
/// reference engine internals (PLUGIN_SDK_PLAN.md §8.3). Never renumber; only append.
/// </summary>
public enum SdkBlendMode
{
    Normal = 0,
    Multiply = 1,
    Screen = 2,
    Overlay = 3,
    Darken = 4,
    Lighten = 5,
    Add = 6,
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
    Average = 26,
    Negation = 27,
    Reflect = 28,
    Glow = 29,
}

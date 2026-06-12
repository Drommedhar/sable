namespace Sable.Core;

/// <summary>
/// CPU mirror of the WGSL blend contract (composite.wgsl <c>blend()</c>) — used by the
/// brush engine for paint blend modes and by headless tests. Inputs/outputs are 0..1;
/// <paramref name="cb"/> = backdrop, <paramref name="cs"/> = source. Keep in sync with
/// the shader: the <see cref="BlendMode"/> integer values are the shared contract.
/// </summary>
public static class BlendOps
{
    public static (float r, float g, float b) Blend(BlendMode mode, (float r, float g, float b) cb, (float r, float g, float b) cs)
    {
        switch (mode)
        {
            case BlendMode.Multiply: return (cb.r * cs.r, cb.g * cs.g, cb.b * cs.b);
            case BlendMode.Screen: return (Scr(cb.r, cs.r), Scr(cb.g, cs.g), Scr(cb.b, cs.b));
            case BlendMode.Overlay: return (Ovl(cb.r, cs.r), Ovl(cb.g, cs.g), Ovl(cb.b, cs.b));
            case BlendMode.Darken: return (MathF.Min(cb.r, cs.r), MathF.Min(cb.g, cs.g), MathF.Min(cb.b, cs.b));
            case BlendMode.Lighten: return (MathF.Max(cb.r, cs.r), MathF.Max(cb.g, cs.g), MathF.Max(cb.b, cs.b));
            case BlendMode.Add: return (MathF.Min(cb.r + cs.r, 1f), MathF.Min(cb.g + cs.g, 1f), MathF.Min(cb.b + cs.b, 1f));
            case BlendMode.ColorBurn: return (Burn(cb.r, cs.r), Burn(cb.g, cs.g), Burn(cb.b, cs.b));
            case BlendMode.LinearBurn: return (MathF.Max(cb.r + cs.r - 1f, 0f), MathF.Max(cb.g + cs.g - 1f, 0f), MathF.Max(cb.b + cs.b - 1f, 0f));
            case BlendMode.DarkerColor: return Lum(cb) <= Lum(cs) ? cb : cs;
            case BlendMode.ColorDodge: return (Dodge(cb.r, cs.r), Dodge(cb.g, cs.g), Dodge(cb.b, cs.b));
            case BlendMode.LighterColor: return Lum(cb) >= Lum(cs) ? cb : cs;
            case BlendMode.SoftLight: return (Soft(cb.r, cs.r), Soft(cb.g, cs.g), Soft(cb.b, cs.b));
            case BlendMode.HardLight: return (Ovl(cs.r, cb.r), Ovl(cs.g, cb.g), Ovl(cs.b, cb.b));
            case BlendMode.VividLight: return (Vivid(cb.r, cs.r), Vivid(cb.g, cs.g), Vivid(cb.b, cs.b));
            case BlendMode.LinearLight: return (Clamp01(cb.r + 2f * cs.r - 1f), Clamp01(cb.g + 2f * cs.g - 1f), Clamp01(cb.b + 2f * cs.b - 1f));
            case BlendMode.PinLight: return (Pin(cb.r, cs.r), Pin(cb.g, cs.g), Pin(cb.b, cs.b));
            case BlendMode.HardMix: return (Step(Vivid(cb.r, cs.r)), Step(Vivid(cb.g, cs.g)), Step(Vivid(cb.b, cs.b)));
            case BlendMode.Difference: return (MathF.Abs(cb.r - cs.r), MathF.Abs(cb.g - cs.g), MathF.Abs(cb.b - cs.b));
            case BlendMode.Exclusion: return (Excl(cb.r, cs.r), Excl(cb.g, cs.g), Excl(cb.b, cs.b));
            case BlendMode.Subtract: return (MathF.Max(cb.r - cs.r, 0f), MathF.Max(cb.g - cs.g, 0f), MathF.Max(cb.b - cs.b, 0f));
            case BlendMode.Divide: return (Div(cb.r, cs.r), Div(cb.g, cs.g), Div(cb.b, cs.b));
            case BlendMode.Hue: return SetLum(SetSat(cs, Sat(cb)), Lum(cb));
            case BlendMode.Saturation: return SetLum(SetSat(cb, Sat(cs)), Lum(cb));
            case BlendMode.Color: return SetLum(cs, Lum(cb));
            case BlendMode.Luminosity: return SetLum(cb, Lum(cs));
            case BlendMode.Average: return ((cb.r + cs.r) * 0.5f, (cb.g + cs.g) * 0.5f, (cb.b + cs.b) * 0.5f);
            case BlendMode.Negation: return (Neg(cb.r, cs.r), Neg(cb.g, cs.g), Neg(cb.b, cs.b));
            case BlendMode.Reflect: return (Refl(cb.r, cs.r), Refl(cb.g, cs.g), Refl(cb.b, cs.b));
            case BlendMode.Glow: return (Refl(cs.r, cb.r), Refl(cs.g, cb.g), Refl(cs.b, cb.b));
            default: return cs;   // Normal
        }
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
    private static float Step(float v) => v >= 0.5f ? 1f : 0f;
    private static float Scr(float cb, float cs) => cb + cs - cb * cs;
    private static float Excl(float cb, float cs) => cb + cs - 2f * cb * cs;
    private static float Neg(float cb, float cs) => 1f - MathF.Abs(1f - cb - cs);
    private static float Div(float cb, float cs) => Clamp01(cb / MathF.Max(cs, 0.0001f));

    private static float Ovl(float cb, float cs)
        => cb <= 0.5f ? 2f * cb * cs : 1f - 2f * (1f - cb) * (1f - cs);

    private static float Burn(float cb, float cs)
        => cs <= 0f ? 0f : 1f - MathF.Min(1f, (1f - cb) / cs);

    private static float Dodge(float cb, float cs)
        => cs >= 1f ? 1f : MathF.Min(1f, cb / (1f - cs));

    private static float Soft(float cb, float cs)
    {
        if (cs <= 0.5f) return cb - (1f - 2f * cs) * cb * (1f - cb);
        float d = cb <= 0.25f ? ((16f * cb - 12f) * cb + 4f) * cb : MathF.Sqrt(cb);
        return cb + (2f * cs - 1f) * (d - cb);
    }

    private static float Vivid(float cb, float cs)
        => cs <= 0.5f ? Burn(cb, 2f * cs) : Dodge(cb, 2f * cs - 1f);

    private static float Pin(float cb, float cs)
        => cs <= 0.5f ? MathF.Min(cb, 2f * cs) : MathF.Max(cb, 2f * cs - 1f);

    private static float Refl(float cb, float cs)
        => cs >= 1f ? 1f : MathF.Min(1f, cb * cb / (1f - cs));

    // --- non-separable (W3C) helpers ---
    private static float Lum((float r, float g, float b) c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
    private static float Sat((float r, float g, float b) c)
        => MathF.Max(MathF.Max(c.r, c.g), c.b) - MathF.Min(MathF.Min(c.r, c.g), c.b);

    private static (float r, float g, float b) ClipColor((float r, float g, float b) c)
    {
        float l = Lum(c);
        float n = MathF.Min(MathF.Min(c.r, c.g), c.b);
        float x = MathF.Max(MathF.Max(c.r, c.g), c.b);
        var r = c;
        if (n < 0f)
        {
            float k = l / (l - n);
            r = (l + (r.r - l) * k, l + (r.g - l) * k, l + (r.b - l) * k);
        }
        if (x > 1f)
        {
            float k = (1f - l) / (x - l);
            r = (l + (r.r - l) * k, l + (r.g - l) * k, l + (r.b - l) * k);
        }
        return r;
    }

    private static (float r, float g, float b) SetLum((float r, float g, float b) c, float l)
    {
        float d = l - Lum(c);
        return ClipColor((c.r + d, c.g + d, c.b + d));
    }

    private static (float r, float g, float b) SetSat((float r, float g, float b) c, float s)
    {
        float mn = MathF.Min(MathF.Min(c.r, c.g), c.b);
        float mx = MathF.Max(MathF.Max(c.r, c.g), c.b);
        if (mx <= mn) return (0f, 0f, 0f);
        float k = s / (mx - mn);
        return ((c.r - mn) * k, (c.g - mn) * k, (c.b - mn) * k);
    }
}

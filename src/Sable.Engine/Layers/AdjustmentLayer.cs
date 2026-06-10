using System;
using System.Collections.Generic;
using System.Linq;

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
    public float OutBlack { get; set; }         // 0..1 output black level
    public float OutWhite { get; set; } = 1f;   // 0..1 output white level

    // HSL
    public float HueShift { get; set; }         // turns (-0.5..0.5)
    public float Saturation { get; set; } = 1f; // 0..2
    public float Lightness { get; set; }        // -1..1

    // Single-param adjustments
    public float Exposure { get; set; }         // stops (-x..x), gain = 2^Exposure
    public float Vibrance { get; set; }         // -1..1 (smart saturation)
    public float Threshold { get; set; } = 0.5f;// 0..1 luminance cut
    public float Posterize { get; set; } = 6f;  // 2..255 levels

    // Black & White (grayscale luminance weights)
    public float BwR { get; set; } = 0.3f;
    public float BwG { get; set; } = 0.59f;
    public float BwB { get; set; } = 0.11f;

    // White Balance
    public float Temperature { get; set; }      // -1..1 (cool..warm)
    public float Tint { get; set; }             // -1..1 (green..magenta)

    // Shadows / Highlights
    public float Shadows { get; set; }          // -1..1 (+ lifts shadows)
    public float Highlights { get; set; }       // -1..1 (+ recovers highlights)

    // Colour Balance — shadow/mid/highlight RGB shifts (-1..1), 9 values
    public float[] ColorBalance { get; } = new float[9];

    // Channel Mixer — 3x3 row-major (outR=row0·rgb, ...), default identity
    public float[] ChannelMix { get; } = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    // Curves — 4 channels (0=composite/RGB, 1=R, 2=G, 3=B); each a sorted point list (x,y in 0..1).
    public const int CurveChannels = 4;
    public const int LutSize = 256;
    public List<(float x, float y)>[] Curves { get; }
        = Enumerable.Range(0, CurveChannels)
            .Select(_ => new List<(float, float)> { (0f, 0f), (1f, 1f) }).ToArray();

    // Gradient Map — luminance → gradient colour; stops sorted by Pos (0..1). Reuses the curve
    // LUT path on the GPU (channels 1/2/3 = R/G/B output per luma; channel 0 unused).
    public List<(float Pos, byte R, byte G, byte B)> GradientStops { get; }
        = new() { (0f, 0, 0, 0), (1f, 255, 255, 255) };

    public AdjustmentLayer(AdjustmentKind kind = AdjustmentKind.BrightnessContrast)
    {
        Kind = kind;
        Name = kind switch
        {
            AdjustmentKind.BrightnessContrast => "Brightness/Contrast",
            AdjustmentKind.Levels => "Levels",
            AdjustmentKind.Hsl => "HSL",
            AdjustmentKind.Curves => "Curves",
            AdjustmentKind.Exposure => "Exposure",
            AdjustmentKind.Vibrance => "Vibrance",
            AdjustmentKind.Threshold => "Threshold",
            AdjustmentKind.Posterize => "Posterise",
            AdjustmentKind.Invert => "Invert",
            AdjustmentKind.BlackWhite => "Black & White",
            AdjustmentKind.WhiteBalance => "White Balance",
            AdjustmentKind.ColorBalance => "Colour Balance",
            AdjustmentKind.ChannelMixer => "Channel Mixer",
            AdjustmentKind.ShadowsHighlights => "Shadows / Highlights",
            AdjustmentKind.GradientMap => "Gradient Map",
            _ => "Adjustment"
        };
    }

    protected override Layer CreateClone()
    {
        var c = new AdjustmentLayer(Kind)
        {
            Brightness = Brightness, Contrast = Contrast,
            InBlack = InBlack, InWhite = InWhite, Gamma = Gamma, OutBlack = OutBlack, OutWhite = OutWhite,
            HueShift = HueShift, Saturation = Saturation, Lightness = Lightness,
            Exposure = Exposure, Vibrance = Vibrance, Threshold = Threshold, Posterize = Posterize,
            BwR = BwR, BwG = BwG, BwB = BwB, Temperature = Temperature, Tint = Tint,
            Shadows = Shadows, Highlights = Highlights,
        };
        ColorBalance.CopyTo(c.ColorBalance, 0);
        ChannelMix.CopyTo(c.ChannelMix, 0);
        for (int ch = 0; ch < CurveChannels; ch++) { c.Curves[ch].Clear(); c.Curves[ch].AddRange(Curves[ch]); }
        c.GradientStops.Clear(); c.GradientStops.AddRange(GradientStops);
        return c;
    }

    /// <summary>Fill a 4×256 LUT (channel-major, values 0..1) from the curve control points.</summary>
    public void BuildLut(Span<float> lut)
    {
        for (int ch = 0; ch < CurveChannels; ch++)
        {
            var pts = Curves[ch];
            int baseI = ch * LutSize;
            for (int i = 0; i < LutSize; i++)
            {
                float x = i / (float)(LutSize - 1);
                lut[baseI + i] = Math.Clamp(EvalCurve(pts, x), 0f, 1f);
            }
        }
    }

    /// <summary>Evaluate one channel's curve at x (0..1), clamped — matches the GPU LUT.</summary>
    public float EvalChannel(int ch, float x) => Math.Clamp(EvalCurve(Curves[ch], x), 0f, 1f);

    /// <summary>
    /// Fill the shared 4×256 LUT from <see cref="GradientStops"/> for the Gradient Map kind:
    /// channels 1/2/3 hold the gradient's R/G/B (0..1) per luminance index; channel 0 is identity.
    /// </summary>
    public void BuildGradientLut(Span<float> lut)
    {
        var stops = GradientStops;
        for (int i = 0; i < LutSize; i++)
        {
            float t = i / (float)(LutSize - 1);
            var (r, g, b) = SampleGradient(stops, t);
            lut[i] = t;                       // ch0 unused by the shader case — keep identity
            lut[1 * LutSize + i] = r;
            lut[2 * LutSize + i] = g;
            lut[3 * LutSize + i] = b;
        }
    }

    /// <summary>Linear-interpolate the (sorted) gradient stops at t (0..1) → 0..1 floats.</summary>
    public static (float r, float g, float b) SampleGradient(
        List<(float Pos, byte R, byte G, byte B)> stops, float t)
    {
        if (stops.Count == 0) return (t, t, t);
        if (t <= stops[0].Pos || stops.Count == 1)
            return (stops[0].R / 255f, stops[0].G / 255f, stops[0].B / 255f);
        var last = stops[^1];
        if (t >= last.Pos) return (last.R / 255f, last.G / 255f, last.B / 255f);
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var a = stops[i]; var b = stops[i + 1];
            if (t > b.Pos) continue;
            float f = b.Pos - a.Pos < 1e-6f ? 1f : (t - a.Pos) / (b.Pos - a.Pos);
            return ((a.R + (b.R - a.R) * f) / 255f,
                    (a.G + (b.G - a.G) * f) / 255f,
                    (a.B + (b.B - a.B) * f) / 255f);
        }
        return (last.R / 255f, last.G / 255f, last.B / 255f);
    }

    /// <summary>Monotone (Catmull-Rom, slope-limited) interpolation of a sorted point list at x.</summary>
    private static float EvalCurve(List<(float x, float y)> pts, float x)
    {
        int n = pts.Count;
        if (n == 0) return x;
        if (n == 1) return pts[0].y;
        if (x <= pts[0].x) return pts[0].y;
        if (x >= pts[n - 1].x) return pts[n - 1].y;

        int i = 0;
        while (i < n - 1 && x > pts[i + 1].x) i++;
        var p1 = pts[i];
        var p2 = pts[i + 1];
        float dx = p2.x - p1.x;
        if (dx <= 1e-6f) return p2.y;
        float t = (x - p1.x) / dx;

        var p0 = i > 0 ? pts[i - 1] : p1;
        var p3 = i + 2 < n ? pts[i + 2] : p2;
        // tangents (Catmull-Rom), scaled to this segment
        float m1 = (p2.y - p0.y) / Math.Max(1e-6f, p2.x - p0.x) * dx;
        float m2 = (p3.y - p1.y) / Math.Max(1e-6f, p3.x - p1.x) * dx;
        float t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * p1.y + (t3 - 2 * t2 + t) * m1
             + (-2 * t3 + 3 * t2) * p2.y + (t3 - t2) * m2;
    }

    /// <summary>True if any channel deviates from identity (more than the two endpoints).</summary>
    public bool CurvesAreIdentity()
    {
        foreach (var ch in Curves)
            if (ch.Count != 2 || ch[0] != (0f, 0f) || ch[1] != (1f, 1f)) return false;
        return true;
    }

    /// <summary>Fill p0..p5 (6 floats) for the shader based on Kind.</summary>
    public void PackParams(Span<float> p)
    {
        p.Clear();
        switch (Kind)
        {
            case AdjustmentKind.Levels:
                p[0] = InBlack; p[1] = InWhite; p[2] = Gamma; p[3] = OutBlack; p[4] = OutWhite;
                break;
            case AdjustmentKind.Hsl:
                p[0] = HueShift; p[1] = Saturation; p[2] = Lightness;
                break;
            case AdjustmentKind.Exposure:  p[0] = Exposure; break;
            case AdjustmentKind.Vibrance:  p[0] = Vibrance; break;
            case AdjustmentKind.Threshold: p[0] = Threshold; break;
            case AdjustmentKind.Posterize: p[0] = Posterize; break;
            case AdjustmentKind.Invert:    break;
            case AdjustmentKind.BlackWhite: p[0] = BwR; p[1] = BwG; p[2] = BwB; break;
            case AdjustmentKind.WhiteBalance: p[0] = Temperature; p[1] = Tint; break;
            case AdjustmentKind.ColorBalance: ColorBalance.AsSpan(0, 9).CopyTo(p); break;
            case AdjustmentKind.ChannelMixer: ChannelMix.AsSpan(0, 9).CopyTo(p); break;
            case AdjustmentKind.ShadowsHighlights: p[0] = Shadows; p[1] = Highlights; break;
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
    Curves = 3,
    Exposure = 4,
    Vibrance = 5,
    Threshold = 6,
    Posterize = 7,
    Invert = 8,
    BlackWhite = 9,
    WhiteBalance = 10,
    ColorBalance = 11,
    ChannelMixer = 12,
    ShadowsHighlights = 13,
    GradientMap = 14,
}

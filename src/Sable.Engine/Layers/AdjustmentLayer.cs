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

    // Curves — 4 channels (0=composite/RGB, 1=R, 2=G, 3=B); each a sorted point list (x,y in 0..1).
    public const int CurveChannels = 4;
    public const int LutSize = 256;
    public List<(float x, float y)>[] Curves { get; }
        = Enumerable.Range(0, CurveChannels)
            .Select(_ => new List<(float, float)> { (0f, 0f), (1f, 1f) }).ToArray();

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
            _ => "Adjustment"
        };
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
}

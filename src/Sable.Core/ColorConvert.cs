using System;

namespace Sable.Core;

/// <summary>
/// Colour-space conversions for the colour panel (PLAN §16.11): sRGB ↔ HSL / CMYK / LAB. RGB is
/// 0..255 bytes; HSL = (H 0..360, S 0..1, L 0..1); CMYK = 0..1; LAB = (L 0..100, a/b ≈ ±128, D65).
/// Pure — unit-testable. (Not colour-managed; assumes sRGB — the ICC pipeline is a later phase.)
/// </summary>
public static class ColorConvert
{
    // ---- HSL ----
    public static (double h, double s, double l) RgbToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2, h = 0, s = 0;
        double d = max - min;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == rd) h = (gd - bd) / d + (gd < bd ? 6 : 0);
            else if (max == gd) h = (bd - rd) / d + 2;
            else h = (rd - gd) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    public static (byte r, byte g, byte b) HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1); l = Math.Clamp(l, 0, 1);
        if (s < 1e-9) { byte v = B(l); return (v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double hk = h / 360;
        return (B(Hue(p, q, hk + 1.0 / 3)), B(Hue(p, q, hk)), B(Hue(p, q, hk - 1.0 / 3)));
    }

    private static double Hue(double p, double q, double t)
    {
        t = ((t % 1) + 1) % 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    // ---- CMYK ----
    public static (double c, double m, double y, double k) RgbToCmyk(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double k = 1 - Math.Max(rd, Math.Max(gd, bd));
        if (k >= 1 - 1e-9) return (0, 0, 0, 1);
        double c = (1 - rd - k) / (1 - k);
        double m = (1 - gd - k) / (1 - k);
        double y = (1 - bd - k) / (1 - k);
        return (c, m, y, k);
    }

    public static (byte r, byte g, byte b) CmykToRgb(double c, double m, double y, double k)
    {
        c = Math.Clamp(c, 0, 1); m = Math.Clamp(m, 0, 1); y = Math.Clamp(y, 0, 1); k = Math.Clamp(k, 0, 1);
        return (B((1 - c) * (1 - k)), B((1 - m) * (1 - k)), B((1 - y) * (1 - k)));
    }

    // ---- LAB (via XYZ, D65) ----
    public static (double L, double a, double b) RgbToLab(byte r, byte g, byte b)
    {
        double rl = Lin(r / 255.0), gl = Lin(g / 255.0), bl = Lin(b / 255.0);
        double x = (rl * 0.4124 + gl * 0.3576 + bl * 0.1805) / 0.95047;
        double y = (rl * 0.2126 + gl * 0.7152 + bl * 0.0722);
        double z = (rl * 0.0193 + gl * 0.1192 + bl * 0.9505) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    public static (byte r, byte g, byte b) LabToRgb(double L, double a, double bb)
    {
        double fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - bb / 200;
        double x = Finv(fx) * 0.95047, y = Finv(fy), z = Finv(fz) * 1.08883;
        double rl = x * 3.2406 + y * -1.5372 + z * -0.4986;
        double gl = x * -0.9689 + y * 1.8758 + z * 0.0415;
        double bl = x * 0.0557 + y * -0.2040 + z * 1.0570;
        return (B(Gam(rl)), B(Gam(gl)), B(Gam(bl)));
    }

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double Gam(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(Math.Clamp(c, 0, 1), 1 / 2.4) - 0.055;
    private static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
    private static double Finv(double t) { double t3 = t * t * t; return t3 > 0.008856 ? t3 : (t - 16.0 / 116) / 7.787; }
    private static byte B(double v) => (byte)Math.Clamp(v * 255 + 0.5, 0, 255);
}

namespace Sable.Tools;

/// <summary>Gradient geometry: how the drag line maps to the 0..1 ramp.</summary>
public enum GradientShape
{
    Linear = 0,      // along the drag line
    Radial = 1,      // distance from the start point (drag length = radius)
    Conical = 2,     // angle around the start point (drag sets the 0 angle)
    Reflected = 3,   // linear, mirrored about the start point
    Diamond = 4,     // L1 distance in the drag frame (square rotated 45°)
}

/// <summary>
/// Gradient fill. Paints the gradient ramp across the drag from (x0,y0) to (x1,y1)
/// with the chosen <see cref="GradientShape"/> (src-over into the RGBA8 buffer).
/// Honors an optional clip rect + coverage mask (selection), multiplying alpha by
/// mask coverage so it respects feathered edges.
/// Returns the count of pixels touched (0 = no-op / zero-length drag).
/// </summary>
public static class GradientTool
{
    /// <summary>Two-stop convenience: foreground colour → transparent.</summary>
    public static int Apply(byte[] px, int w, int h, double x0, double y0, double x1, double y1,
        byte r, byte g, byte b, (int X, int Y, int W, int H)? clip = null,
        byte[]? mask = null, int maskW = 0)
        => Apply(px, w, h, x0, y0, x1, y1, GradientDef.ForegroundToTransparent(r, g, b), clip, mask, maskW);

    /// <summary>Paint a multi-stop gradient (<paramref name="def"/>) across the drag line.</summary>
    public static int Apply(byte[] px, int w, int h, double x0, double y0, double x1, double y1,
        GradientDef def, (int X, int Y, int W, int H)? clip = null,
        byte[]? mask = null, int maskW = 0, GradientShape shape = GradientShape.Linear)
    {
        double dxl = x1 - x0, dyl = y1 - y0;
        double len2 = dxl * dxl + dyl * dyl;
        if (len2 < 1e-6) return 0;   // zero-length drag
        if (maskW == 0) maskW = w;

        int minX = 0, minY = 0, maxX = w - 1, maxY = h - 1;
        if (clip is { } c)
        {
            minX = Math.Max(0, c.X); minY = Math.Max(0, c.Y);
            maxX = Math.Min(w - 1, c.X + c.W - 1); maxY = Math.Min(h - 1, c.Y + c.H - 1);
        }

        int changed = 0;

        double len = Math.Sqrt(len2);
        double dragAngle = Math.Atan2(dyl, dxl);

        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            double px0 = x + 0.5 - x0, py0 = y + 0.5 - y0;
            double t;
            switch (shape)
            {
                case GradientShape.Radial:
                    t = Math.Sqrt(px0 * px0 + py0 * py0) / len;
                    break;
                case GradientShape.Conical:
                {
                    double a = Math.Atan2(py0, px0) - dragAngle;
                    t = a / (2 * Math.PI);
                    t -= Math.Floor(t);   // wrap to 0..1 around the start point
                    break;
                }
                case GradientShape.Reflected:
                    t = Math.Abs((px0 * dxl + py0 * dyl) / len2);
                    break;
                case GradientShape.Diamond:
                {
                    double along = (px0 * dxl + py0 * dyl) / len;         // distance along the drag
                    double perp = (px0 * -dyl + py0 * dxl) / len;         // distance across it
                    t = (Math.Abs(along) + Math.Abs(perp)) / len;
                    break;
                }
                default:   // Linear: project pixel centre onto the gradient line
                    t = (px0 * dxl + py0 * dyl) / len2;
                    break;
            }
            t = Math.Clamp(t, 0.0, 1.0);
            var (cr, cg, cb, ca) = def.Sample((float)t);
            float sr = cr / 255f, sg = cg / 255f, sb = cb / 255f;
            float sa = ca / 255f;

            if (mask is not null) sa *= mask[y * maskW + x] / 255f;
            if (sa <= 0f) continue;

            int i = (y * w + x) * 4;
            float dr = px[i] / 255f, dg = px[i + 1] / 255f, db = px[i + 2] / 255f, da = px[i + 3] / 255f;
            float outA = sa + da * (1f - sa);
            if (outA <= 0f) { px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0; changed++; continue; }
            float outR = (sr * sa + dr * da * (1f - sa)) / outA;
            float outG = (sg * sa + dg * da * (1f - sa)) / outA;
            float outB = (sb * sa + db * da * (1f - sa)) / outA;
            px[i] = (byte)(Math.Clamp(outR, 0f, 1f) * 255f + 0.5f);
            px[i + 1] = (byte)(Math.Clamp(outG, 0f, 1f) * 255f + 0.5f);
            px[i + 2] = (byte)(Math.Clamp(outB, 0f, 1f) * 255f + 0.5f);
            px[i + 3] = (byte)(Math.Clamp(outA, 0f, 1f) * 255f + 0.5f);
            changed++;
        }
        return changed;
    }
}

namespace Sable.Tools;

/// <summary>
/// Linear gradient fill. Paints a foreground→transparent ramp across the line from
/// (x0,y0) to (x1,y1): full color/alpha at the start, fading to transparent at the
/// end (src-over into the RGBA8 buffer). Honors an optional clip rect + coverage mask
/// (selection), multiplying alpha by mask coverage so it respects feathered edges.
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
        byte[]? mask = null, int maskW = 0)
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

        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            // project pixel centre onto the gradient line → t in [0,1]
            double t = ((x + 0.5 - x0) * dxl + (y + 0.5 - y0) * dyl) / len2;
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

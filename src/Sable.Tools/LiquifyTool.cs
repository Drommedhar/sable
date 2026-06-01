using System;

namespace Sable.Tools;

public enum LiquifyMode { Push, Bloat, Pucker, Twirl }

/// <summary>
/// Liquify brush (PLAN §16.9): geometrically displaces pixels under the dab (push / bloat / pucker
/// / twirl). Inverse-warp — each output pixel samples the source at a displaced position, snapshotting
/// the dab region first so the warp is stable within a dab. Destructive on the active pixel layer
/// (the non-destructive mesh version is a follow-up). Bilinear sampling for smooth results.
/// </summary>
public static class LiquifyTool
{
    /// <summary>Apply one dab at (cx,cy). <paramref name="dragX"/>/<paramref name="dragY"/> = pointer
    /// movement since the last dab (used by Push). Strength 0..1, radius in px, hardness 0..1.</summary>
    public static void Stamp(byte[] px, int w, int h, double cx, double cy,
        double dragX, double dragY, LiquifyMode mode, float strength, float radius, float hardness)
    {
        float r = radius;
        int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + r));
        int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + r));
        if (x1 < x0 || y1 < y0) return;

        // snapshot the dab bbox so sampling is stable (no intra-dab feedback)
        int sw = x1 - x0 + 1, sh = y1 - y0 + 1;
        var snap = new byte[sw * sh * 4];
        for (int y = y0; y <= y1; y++)
            Array.Copy(px, (y * w + x0) * 4, snap, (y - y0) * sw * 4, sw * 4);

        float inner = r * Math.Clamp(hardness, 0f, 0.95f);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float ddx = (float)(x - cx), ddy = (float)(y - cy);
            float dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (dist > r) continue;
            float t = dist <= inner ? 1f : 1f - (dist - inner) / MathF.Max(1e-3f, r - inner);
            float f = Math.Clamp(t, 0f, 1f);
            f = f * f * (3f - 2f * f) * strength;     // smooth falloff × strength

            // source position to sample FROM (inverse warp)
            float sxp = x, syp = y;
            switch (mode)
            {
                case LiquifyMode.Push:
                    sxp = (float)(x - dragX * f); syp = (float)(y - dragY * f); break;
                case LiquifyMode.Bloat:   // pixels move outward → sample nearer the centre
                    sxp = (float)(x - ddx * f * 0.6f); syp = (float)(y - ddy * f * 0.6f); break;
                case LiquifyMode.Pucker:  // pixels move inward → sample farther from the centre
                    sxp = (float)(x + ddx * f * 0.6f); syp = (float)(y + ddy * f * 0.6f); break;
                case LiquifyMode.Twirl:
                {
                    float a = f * 2.2f;   // radians
                    float cs = MathF.Cos(a), sn = MathF.Sin(a);
                    float rx = cs * ddx - sn * ddy, ry = sn * ddx + cs * ddy;
                    sxp = (float)(cx + rx); syp = (float)(cy + ry); break;
                }
            }
            WriteBilinear(px, w, h, x, y, snap, x0, y0, sw, sh, sxp, syp);
        }
    }

    private static void WriteBilinear(byte[] dst, int w, int h, int dx, int dy,
        byte[] snap, int sx0, int sy0, int sw, int sh, float sx, float sy)
    {
        // sample from the snapshot where available (covers the warped neighbourhood), else clamp
        int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
        float fx = sx - x0, fy = sy - y0;
        Span<byte> c = stackalloc byte[4];
        for (int k = 0; k < 4; k++)
        {
            float v00 = Sample(snap, sx0, sy0, sw, sh, dst, w, h, x0, y0, k);
            float v10 = Sample(snap, sx0, sy0, sw, sh, dst, w, h, x0 + 1, y0, k);
            float v01 = Sample(snap, sx0, sy0, sw, sh, dst, w, h, x0, y0 + 1, k);
            float v11 = Sample(snap, sx0, sy0, sw, sh, dst, w, h, x0 + 1, y0 + 1, k);
            float v = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) + v01 * (1 - fx) * fy + v11 * fx * fy;
            c[k] = (byte)Math.Clamp(v + 0.5f, 0, 255);
        }
        int di = (dy * w + dx) * 4;
        dst[di] = c[0]; dst[di + 1] = c[1]; dst[di + 2] = c[2]; dst[di + 3] = c[3];
    }

    private static float Sample(byte[] snap, int sx0, int sy0, int sw, int sh,
        byte[] full, int w, int h, int x, int y, int k)
    {
        if (x >= sx0 && y >= sy0 && x < sx0 + sw && y < sy0 + sh)
            return snap[((y - sy0) * sw + (x - sx0)) * 4 + k];
        // outside the snapshot: read the live buffer (clamped)
        x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
        return full[(y * w + x) * 4 + k];
    }
}

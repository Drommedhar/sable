using System;

namespace Sable.Tools;

/// <summary>
/// Mesh warp (PLAN §16.9): deform a pixel buffer through a control-point grid. Each grid cell is
/// split into two triangles; the destination triangle is rasterized and each covered pixel maps
/// (via barycentric coords) back to the matching source triangle for a bilinear sample. Destructive
/// (one-shot Apply); the non-destructive GPU mesh is a follow-up. Grids are <c>gx×gy</c> points
/// row-major. <paramref name="srcPts"/> = the undeformed grid, <paramref name="dstPts"/> = dragged.
/// </summary>
public static class MeshWarpTool
{
    public static float[] Warp(float[] src, int w, int h, int gx, int gy,
        (float X, float Y)[] srcPts, (float X, float Y)[] dstPts)
    {
        var dst = new float[w * h * 4];
        for (int cy = 0; cy < gy - 1; cy++)
        for (int cx = 0; cx < gx - 1; cx++)
        {
            int a = cy * gx + cx, b = a + 1, c = a + gx, d = c + 1;   // TL,TR,BL,BR of the cell
            Triangle(src, dst, w, h, srcPts[a], srcPts[b], srcPts[c], dstPts[a], dstPts[b], dstPts[c]);
            Triangle(src, dst, w, h, srcPts[b], srcPts[d], srcPts[c], dstPts[b], dstPts[d], dstPts[c]);
        }
        return dst;
    }

    private static void Triangle(float[] src, float[] dst, int w, int h,
        (float X, float Y) s0, (float X, float Y) s1, (float X, float Y) s2,
        (float X, float Y) d0, (float X, float Y) d1, (float X, float Y) d2)
    {
        int minx = Math.Max(0, (int)MathF.Floor(Math.Min(d0.X, Math.Min(d1.X, d2.X))));
        int maxx = Math.Min(w - 1, (int)MathF.Ceiling(Math.Max(d0.X, Math.Max(d1.X, d2.X))));
        int miny = Math.Max(0, (int)MathF.Floor(Math.Min(d0.Y, Math.Min(d1.Y, d2.Y))));
        int maxy = Math.Min(h - 1, (int)MathF.Ceiling(Math.Max(d0.Y, Math.Max(d1.Y, d2.Y))));
        if (minx > maxx || miny > maxy) return;

        float det = (d1.Y - d2.Y) * (d0.X - d2.X) + (d2.X - d1.X) * (d0.Y - d2.Y);
        if (MathF.Abs(det) < 1e-6f) return;
        float invDet = 1f / det;

        for (int y = miny; y <= maxy; y++)
        for (int x = minx; x <= maxx; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float l0 = ((d1.Y - d2.Y) * (px - d2.X) + (d2.X - d1.X) * (py - d2.Y)) * invDet;
            float l1 = ((d2.Y - d0.Y) * (px - d2.X) + (d0.X - d2.X) * (py - d2.Y)) * invDet;
            float l2 = 1f - l0 - l1;
            if (l0 < -0.001f || l1 < -0.001f || l2 < -0.001f) continue;   // outside the triangle
            // matching source position
            float sx = l0 * s0.X + l1 * s1.X + l2 * s2.X;
            float sy = l0 * s0.Y + l1 * s1.Y + l2 * s2.Y;
            SampleBilinear(src, w, h, sx, sy, dst, (y * w + x) * 4);
        }
    }

    private static void SampleBilinear(float[] src, int w, int h, float sx, float sy, float[] dst, int di)
    {
        int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
        float fx = sx - x0, fy = sy - y0;
        for (int k = 0; k < 4; k++)
        {
            float v00 = Px(src, w, h, x0, y0, k), v10 = Px(src, w, h, x0 + 1, y0, k);
            float v01 = Px(src, w, h, x0, y0 + 1, k), v11 = Px(src, w, h, x0 + 1, y0 + 1, k);
            float v = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) + v01 * (1 - fx) * fy + v11 * fx * fy;
            dst[di + k] = MathF.Max(v, 0f);   // no upper clamp → HDR preserved
        }
    }

    private static float Px(float[] src, int w, int h, int x, int y, int k)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return 0;
        return src[(y * w + x) * 4 + k];
    }
}

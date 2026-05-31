using System;
using System.Collections.Generic;

namespace Sable.Engine.Layers;

/// <summary>Stroke end style for open paths.</summary>
public enum LineCap { Butt, Round, Square }

/// <summary>Stroke corner style between segments.</summary>
public enum LineJoin { Miter, Round, Bevel }

/// <summary>
/// Shared CPU rasteriser for vector geometry (paths + parametric shapes): even-odd polygon
/// fill (4× vertical supersampled for AA) and a distance-field polyline stroke with optional
/// dashing. Both src-over straight-alpha RGBA8 into a doc-sized buffer, so a caller fills first
/// then strokes on top. Bbox-limited; geometry is in document pixels.
/// </summary>
public static class VectorRaster
{
    /// <summary>Even-odd fill of a closed polygon (straight-alpha src-over).</summary>
    public static void Fill(byte[] dst, int dw, int dh, IReadOnlyList<(double X, double Y)> poly, byte r, byte g, byte b, byte a)
    {
        int n = poly.Count;
        if (n < 3 || a == 0) return;
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        for (int i = 0; i < n; i++) { var (px, py) = poly[i]; minx = Math.Min(minx, px); miny = Math.Min(miny, py); maxx = Math.Max(maxx, px); maxy = Math.Max(maxy, py); }
        int lx = Math.Max(0, (int)Math.Floor(minx)), rx = Math.Min(dw - 1, (int)Math.Ceiling(maxx));
        int ty = Math.Max(0, (int)Math.Floor(miny)), by = Math.Min(dh - 1, (int)Math.Ceiling(maxy));
        if (lx > rx || ty > by) return;

        var xs = new List<double>(16);
        for (int y = ty; y <= by; y++)
        {
            for (int x = lx; x <= rx; x++)
            {
                float cov = 0f;
                for (int s = 0; s < 4; s++)
                {
                    double yc = y + (s + 0.5) / 4.0;
                    xs.Clear();
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        double yi = poly[i].Y, yj = poly[j].Y;
                        if ((yi > yc) != (yj > yc))
                        {
                            double xi = poly[i].X, xj = poly[j].X;
                            xs.Add(xi + (yc - yi) / (yj - yi) * (xj - xi));
                        }
                    }
                    xs.Sort();
                    double fx = x + 0.5;
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                        if (fx >= xs[k] && fx < xs[k + 1]) { cov += 0.25f; break; }
                }
                if (cov > 0f) SrcOver(dst, (y * dw + x) * 4, r, g, b, cov * (a / 255f));
            }
        }
    }

    /// <summary>Even-odd fill across MANY closed contours at once (so holes/counters work, e.g. the
    /// inside of an "O" or "8"). Each contour is a polyline; crossings from all of them are merged
    /// per scanline. Straight-alpha src-over.</summary>
    public static void FillMulti(byte[] dst, int dw, int dh, IReadOnlyList<IReadOnlyList<(double X, double Y)>> contours,
        byte r, byte g, byte b, byte a)
    {
        if (a == 0 || contours.Count == 0) return;
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (var c in contours)
            foreach (var (px, py) in c) { minx = Math.Min(minx, px); miny = Math.Min(miny, py); maxx = Math.Max(maxx, px); maxy = Math.Max(maxy, py); }
        if (minx > maxx) return;
        int lx = Math.Max(0, (int)Math.Floor(minx)), rx = Math.Min(dw - 1, (int)Math.Ceiling(maxx));
        int ty = Math.Max(0, (int)Math.Floor(miny)), by = Math.Min(dh - 1, (int)Math.Ceiling(maxy));
        if (lx > rx || ty > by) return;

        var xs = new List<double>(32);
        for (int y = ty; y <= by; y++)
        {
            // build a per-subscan coverage by accumulating crossings across all contours
            for (int x = lx; x <= rx; x++)
            {
                float cov = 0f;
                for (int s = 0; s < 4; s++)
                {
                    double yc = y + (s + 0.5) / 4.0;
                    xs.Clear();
                    foreach (var c in contours)
                    {
                        int n = c.Count;
                        if (n < 3) continue;
                        for (int i = 0, j = n - 1; i < n; j = i++)
                        {
                            double yi = c[i].Y, yj = c[j].Y;
                            if ((yi > yc) != (yj > yc))
                            {
                                double xi = c[i].X, xj = c[j].X;
                                xs.Add(xi + (yc - yi) / (yj - yi) * (xj - xi));
                            }
                        }
                    }
                    xs.Sort();
                    double fx = x + 0.5;
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                        if (fx >= xs[k] && fx < xs[k + 1]) { cov += 0.25f; break; }
                }
                if (cov > 0f) SrcOver(dst, (y * dw + x) * 4, r, g, b, cov * (a / 255f));
            }
        }
    }

    /// <summary>
    /// Stroke a polyline with line caps (open ends) + line joins (corners) + optional dashing.
    /// Body = rectangle bands per segment; caps/joins add round discs, square boxes, miter spikes
    /// or bevel triangles. Round cap+join (the default) reproduce the old distance-field look.
    /// </summary>
    public static void Stroke(byte[] dst, int dw, int dh, IReadOnlyList<(double X, double Y)> pts, bool closed,
        double width, byte r, byte g, byte b, byte a, bool dash = false, double dashLen = 0, double gapLen = 0,
        LineCap cap = LineCap.Round, LineJoin join = LineJoin.Round, double miterLimit = 4)
    {
        if (pts.Count < 2 || width <= 0 || a == 0) return;
        double halfW = Math.Max(0.5, width / 2);

        var bodies = new List<(double ax, double ay, double bx, double by)>();
        var joints = new List<(double vx, double vy, double ix, double iy, double ox, double oy)>();
        var caps = new List<(double ex, double ey, double tx, double ty)>();

        void AddPolyline(IReadOnlyList<(double X, double Y)> p, bool cl)
        {
            int n = p.Count;
            if (n < 2) return;
            int last = cl ? n : n - 1;
            for (int i = 0; i < last; i++) { var A = p[i]; var B = p[(i + 1) % n]; bodies.Add((A.X, A.Y, B.X, B.Y)); }
            int js = cl ? 0 : 1, je = cl ? n : n - 1;
            for (int i = js; i < je; i++)
            {
                var V = p[i]; var Pp = p[(i - 1 + n) % n]; var Nn = p[(i + 1) % n];
                var (ix, iy) = Unit(V.X - Pp.X, V.Y - Pp.Y);
                var (ox, oy) = Unit(Nn.X - V.X, Nn.Y - V.Y);
                joints.Add((V.X, V.Y, ix, iy, ox, oy));
            }
            if (!cl)
            {
                var (t0x, t0y) = Unit(p[0].X - p[1].X, p[0].Y - p[1].Y);
                caps.Add((p[0].X, p[0].Y, t0x, t0y));
                var (t1x, t1y) = Unit(p[n - 1].X - p[n - 2].X, p[n - 1].Y - p[n - 2].Y);
                caps.Add((p[n - 1].X, p[n - 1].Y, t1x, t1y));
            }
        }

        if (dash && dashLen > 0)
            foreach (var (sx, sy, ex, ey) in DashSegments(pts, closed, dashLen, Math.Max(0, gapLen)))
                AddPolyline(new[] { (sx, sy), (ex, ey) }, false);   // each dash gets caps, no joins
        else
            AddPolyline(pts, closed);
        if (bodies.Count == 0) return;

        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (var (ax, ay, bx, by) in bodies)
        {
            minx = Math.Min(minx, Math.Min(ax, bx)); maxx = Math.Max(maxx, Math.Max(ax, bx));
            miny = Math.Min(miny, Math.Min(ay, by)); maxy = Math.Max(maxy, Math.Max(ay, by));
        }
        int extra = join == LineJoin.Miter ? (int)Math.Ceiling(halfW * miterLimit) : 0;
        if (cap == LineCap.Square) extra = Math.Max(extra, (int)Math.Ceiling(halfW));
        int pad = (int)Math.Ceiling(halfW) + extra + 1;
        int lx = Math.Max(0, (int)Math.Floor(minx) - pad), rx = Math.Min(dw - 1, (int)Math.Ceiling(maxx) + pad);
        int ty = Math.Max(0, (int)Math.Floor(miny) - pad), by2 = Math.Min(dh - 1, (int)Math.Ceiling(maxy) + pad);
        if (lx > rx || ty > by2) return;

        for (int y = ty; y <= by2; y++)
        for (int x = lx; x <= rx; x++)
        {
            double fx = x + 0.5, fy = y + 0.5;
            float cov = 0f;
            // body: rectangle band of each segment
            foreach (var (ax, ay, bx, bcy) in bodies)
            {
                double cv = BandCoverage(fx, fy, ax, ay, bx, bcy, halfW);
                if (cv > cov) cov = (float)cv;
                if (cov >= 1f) break;
            }
            // joins
            if (cov < 1f)
                foreach (var j in joints)
                {
                    double cv = join switch
                    {
                        LineJoin.Round => Math.Clamp(halfW - Dist(fx, fy, j.vx, j.vy) + 0.5, 0, 1),
                        LineJoin.Bevel => BevelCoverage(fx, fy, j, halfW),
                        _ => MiterCoverage(fx, fy, j, halfW, miterLimit),
                    };
                    if (cv > cov) cov = (float)cv;
                    if (cov >= 1f) break;
                }
            // caps
            if (cov < 1f && cap != LineCap.Butt)
                foreach (var c in caps)
                {
                    double cv = cap == LineCap.Round
                        ? Math.Clamp(halfW - Dist(fx, fy, c.ex, c.ey) + 0.5, 0, 1)
                        : SquareCapCoverage(fx, fy, c, halfW);
                    if (cv > cov) cov = (float)cv;
                    if (cov >= 1f) break;
                }
            if (cov > 0f) SrcOver(dst, (y * dw + x) * 4, r, g, b, cov * (a / 255f));
        }
    }

    private static (double, double) Unit(double x, double y)
    {
        double l = Math.Sqrt(x * x + y * y);
        return l > 1e-9 ? (x / l, y / l) : (0, 0);
    }

    private static double Dist(double px, double py, double qx, double qy)
        => Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));

    /// <summary>Coverage of a pixel within a segment's straight rectangle band (no end rounding).</summary>
    private static double BandCoverage(double px, double py, double ax, double ay, double bx, double by, double halfW)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return 0;
        double t = ((px - ax) * dx + (py - ay) * dy) / len2;
        if (t < 0 || t > 1) return 0;
        double qx = ax + t * dx, qy = ay + t * dy;
        return Math.Clamp(halfW - Dist(px, py, qx, qy) + 0.5, 0, 1);
    }

    private static double SquareCapCoverage(double px, double py, (double ex, double ey, double tx, double ty) c, double halfW)
    {
        double rx = px - c.ex, ry = py - c.ey;
        double along = rx * c.tx + ry * c.ty;        // outward distance past the end
        double perp = Math.Abs(rx * -c.ty + ry * c.tx);
        if (along < 0) return 0;
        return Math.Min(Math.Clamp(halfW - along + 0.5, 0, 1), Math.Clamp(halfW - perp + 0.5, 0, 1));
    }

    private static double BevelCoverage(double px, double py, (double vx, double vy, double ix, double iy, double ox, double oy) j, double halfW)
    {
        // outer triangle on each side: (V, V+perp(in)*hw, V+perp(out)*hw)
        for (int s = -1; s <= 1; s += 2)
        {
            double p0x = j.vx + (-j.iy) * halfW * s, p0y = j.vy + (j.ix) * halfW * s;
            double p1x = j.vx + (-j.oy) * halfW * s, p1y = j.vy + (j.ox) * halfW * s;
            if (PointInTri(px, py, j.vx, j.vy, p0x, p0y, p1x, p1y)) return 1;
        }
        return 0;
    }

    private static double MiterCoverage(double px, double py, (double vx, double vy, double ix, double iy, double ox, double oy) j, double halfW, double limit)
    {
        for (int s = -1; s <= 1; s += 2)
        {
            double p0x = j.vx + (-j.iy) * halfW * s, p0y = j.vy + (j.ix) * halfW * s;
            double p1x = j.vx + (-j.oy) * halfW * s, p1y = j.vy + (j.ox) * halfW * s;
            // intersect offset line through p0 (dir in) with offset line through p1 (dir out)
            if (LineIntersect(p0x, p0y, j.ix, j.iy, p1x, p1y, j.ox, j.oy, out double mx, out double my)
                && Dist(mx, my, j.vx, j.vy) <= limit * halfW)
            {
                if (PointInTri(px, py, j.vx, j.vy, p0x, p0y, mx, my) || PointInTri(px, py, j.vx, j.vy, mx, my, p1x, p1y)) return 1;
            }
            else if (PointInTri(px, py, j.vx, j.vy, p0x, p0y, p1x, p1y)) return 1;   // miter limit → bevel
        }
        return 0;
    }

    private static bool LineIntersect(double ax, double ay, double adx, double ady, double bx, double by, double bdx, double bdy, out double ix, out double iy)
    {
        ix = iy = 0;
        double denom = adx * bdy - ady * bdx;
        if (Math.Abs(denom) < 1e-9) return false;
        double t = ((bx - ax) * bdy - (by - ay) * bdx) / denom;
        ix = ax + t * adx; iy = ay + t * ady;
        return true;
    }

    private static bool PointInTri(double px, double py, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = Sign(px, py, ax, ay, bx, by), d2 = Sign(px, py, bx, by, cx, cy), d3 = Sign(px, py, cx, cy, ax, ay);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static double Sign(double px, double py, double ax, double ay, double bx, double by)
        => (px - bx) * (ay - by) - (ax - bx) * (py - by);

    private static List<(double, double, double, double)> SolidSegments(IReadOnlyList<(double X, double Y)> pts, bool closed)
    {
        var segs = new List<(double, double, double, double)>(pts.Count);
        int n = pts.Count, last = closed ? n : n - 1;
        for (int i = 0; i < last; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % n];
            segs.Add((a.X, a.Y, b.X, b.Y));
        }
        return segs;
    }

    /// <summary>Split the polyline into "on" dash sub-segments by walking arc length with a carried phase.</summary>
    private static List<(double, double, double, double)> DashSegments(IReadOnlyList<(double X, double Y)> pts, bool closed,
        double dashLen, double gapLen)
    {
        var segs = new List<(double, double, double, double)>();
        double period = dashLen + gapLen;
        double phase = 0;   // distance into the current period
        int n = pts.Count, last = closed ? n : n - 1;
        for (int i = 0; i < last; i++)
        {
            double ax = pts[i].X, ay = pts[i].Y, bx = pts[(i + 1) % n].X, by = pts[(i + 1) % n].Y;
            double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;
            double ux = dx / len, uy = dy / len, pos = 0;
            while (pos < len)
            {
                bool on = phase < dashLen;
                double remainInPhase = on ? dashLen - phase : period - phase;
                double take = Math.Min(remainInPhase, len - pos);
                if (on)
                {
                    double sx = ax + ux * pos, sy = ay + uy * pos;
                    double ex = ax + ux * (pos + take), ey = ay + uy * (pos + take);
                    segs.Add((sx, sy, ex, ey));
                }
                pos += take; phase += take;
                if (phase >= period) phase -= period;
            }
        }
        return segs;
    }

    private static void SrcOver(byte[] dst, int di, byte r, byte g, byte b, float sa)
    {
        if (sa <= 0f) return;
        if (sa > 1f) sa = 1f;
        double da = dst[di + 3] / 255.0;
        double outA = sa + da * (1 - sa);
        if (outA <= 1e-6) return;
        dst[di] = (byte)Math.Clamp((r * sa + dst[di] * da * (1 - sa)) / outA + 0.5, 0, 255);
        dst[di + 1] = (byte)Math.Clamp((g * sa + dst[di + 1] * da * (1 - sa)) / outA + 0.5, 0, 255);
        dst[di + 2] = (byte)Math.Clamp((b * sa + dst[di + 2] * da * (1 - sa)) / outA + 0.5, 0, 255);
        dst[di + 3] = (byte)Math.Clamp(outA * 255 + 0.5, 0, 255);
    }
}

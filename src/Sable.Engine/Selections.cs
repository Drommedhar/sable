namespace Sable.Engine;

/// <summary>How a newly-drawn selection combines with the existing one (PLAN §5A.5).</summary>
public enum SelMode { Replace, Add, Subtract, Intersect }

/// <summary>
/// Builders for a per-pixel selection coverage mask (doc-sized, 1 byte/pixel,
/// 255 = fully selected, 0 = outside). Non-rectangular selections (ellipse, lasso,
/// magic wand) live as a mask on <see cref="Document.SelectionMask"/>; a plain
/// rectangle keeps <see cref="Document.SelectionMask"/> null and uses
/// <see cref="Document.Selection"/> alone (so the rect grips stay editable).
/// The mask is indexed <c>y * docW + x</c>.
/// </summary>
public static class Selections
{
    /// <summary>Filled axis-aligned ellipse inscribed in <paramref name="rect"/>.</summary>
    public static byte[] Ellipse(int docW, int docH, SelRect rect)
    {
        var m = new byte[docW * docH];
        if (rect.W <= 0 || rect.H <= 0) return m;
        double rx = rect.W / 2.0, ry = rect.H / 2.0;
        double cx = rect.X + rx, cy = rect.Y + ry;
        int x0 = Math.Max(0, rect.X), x1 = Math.Min(docW - 1, rect.Right - 1);
        int y0 = Math.Max(0, rect.Y), y1 = Math.Min(docH - 1, rect.Bottom - 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            // sample pixel centre; +0.5 so the ellipse is centred in the cells
            double nx = (x + 0.5 - cx) / rx, ny = (y + 0.5 - cy) / ry;
            if (nx * nx + ny * ny <= 1.0) m[y * docW + x] = 255;
        }
        return m;
    }

    /// <summary>Filled polygon (even-odd rule) from a closed point list (lasso).</summary>
    public static byte[] Polygon(int docW, int docH, IReadOnlyList<(double X, double Y)> pts)
    {
        var m = new byte[docW * docH];
        int n = pts.Count;
        if (n < 3) return m;

        double minYd = double.MaxValue, maxYd = double.MinValue;
        for (int i = 0; i < n; i++) { minYd = Math.Min(minYd, pts[i].Y); maxYd = Math.Max(maxYd, pts[i].Y); }
        int y0 = Math.Max(0, (int)Math.Floor(minYd));
        int y1 = Math.Min(docH - 1, (int)Math.Ceiling(maxYd));

        var xs = new List<double>(n);
        for (int y = y0; y <= y1; y++)
        {
            double yc = y + 0.5;
            xs.Clear();
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = pts[i].Y, yj = pts[j].Y;
                if ((yi > yc) != (yj > yc))
                {
                    double xi = pts[i].X, xj = pts[j].X;
                    xs.Add(xi + (yc - yi) / (yj - yi) * (xj - xi));
                }
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xa = Math.Max(0, (int)Math.Round(xs[k]));
                int xb = Math.Min(docW - 1, (int)Math.Round(xs[k + 1]) - 1);
                for (int x = xa; x <= xb; x++) m[y * docW + x] = 255;
            }
        }
        return m;
    }

    /// <summary>
    /// Magic wand: contiguous region of <paramref name="px"/> (RGBA8, w×h) whose
    /// colour matches the seed within <paramref name="tolerance"/> (per channel).
    /// 4-connected flood. Returns a doc-sized coverage mask.
    /// </summary>
    public static byte[] Wand(byte[] px, int w, int h, int sx, int sy, int tolerance = 32)
    {
        var m = new byte[w * h];
        if (sx < 0 || sy < 0 || sx >= w || sy >= h) return m;
        int seed = (sy * w + sx) * 4;
        byte sr = px[seed], sg = px[seed + 1], sb = px[seed + 2], sa = px[seed + 3];

        bool Match(int i) =>
            Math.Abs(px[i] - sr) <= tolerance &&
            Math.Abs(px[i + 1] - sg) <= tolerance &&
            Math.Abs(px[i + 2] - sb) <= tolerance &&
            Math.Abs(px[i + 3] - sa) <= tolerance;

        var st = new Stack<(int x, int y)>();
        st.Push((sx, sy));
        while (st.Count > 0)
        {
            var (x, y) = st.Pop();
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            int p = y * w + x;
            if (m[p] != 0) continue;             // visited
            if (!Match(p * 4)) continue;
            m[p] = 255;
            st.Push((x - 1, y)); st.Push((x + 1, y));
            st.Push((x, y - 1)); st.Push((x, y + 1));
        }
        return m;
    }

    /// <summary>Coverage mask filled inside an axis-aligned rectangle (for combining a rect marquee).</summary>
    public static byte[] Rect(int docW, int docH, SelRect r)
    {
        var m = new byte[docW * docH];
        int x0 = Math.Max(0, r.X), x1 = Math.Min(docW - 1, r.Right - 1);
        int y0 = Math.Max(0, r.Y), y1 = Math.Min(docH - 1, r.Bottom - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) m[y * docW + x] = 255;
        return m;
    }

    /// <summary>Combine two coverage masks per <paramref name="mode"/> (element-wise; preserves soft edges).</summary>
    public static byte[] Combine(byte[] a, byte[] b, SelMode mode)
    {
        int n = Math.Min(a.Length, b.Length);
        var r = new byte[a.Length];
        for (int i = 0; i < n; i++)
        {
            byte av = a[i], bv = b[i];
            r[i] = mode switch
            {
                SelMode.Add => av >= bv ? av : bv,        // union (max)
                SelMode.Subtract => bv > 0 ? (byte)0 : av, // remove b from a
                SelMode.Intersect => av <= bv ? av : bv,  // intersection (min)
                _ => bv                                    // Replace
            };
        }
        return r;
    }

    /// <summary>Tight bounding rect of the set pixels (for the overlay + dirty bounds), or empty.</summary>
    public static SelRect Bounds(byte[] mask, int w, int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (mask[y * w + x] != 0)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        if (maxX < 0) return new SelRect(0, 0, 0, 0);
        return new SelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}

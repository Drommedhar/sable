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

    /// <summary>
    /// Soften a coverage mask by <paramref name="radius"/> px (separable box blur, H then V)
    /// so edits fade at selection edges instead of a hard 1px cutoff. Returns a new mask;
    /// radius &lt;= 0 returns the input unchanged.
    /// </summary>
    public static byte[] Feather(byte[] mask, int w, int h, int radius)
    {
        if (radius <= 0 || w <= 0 || h <= 0) return mask;
        var tmp = new byte[w * h];
        var outm = new byte[w * h];
        BoxH(mask, tmp, w, h, radius);
        BoxV(tmp, outm, w, h, radius);
        return outm;
    }

    private static void BoxH(byte[] s, byte[] d, int w, int h, int r)
    {
        int win = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int sum = 0;
            for (int k = -r; k <= r; k++) sum += s[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                d[row + x] = (byte)(sum / win);
                int add = Math.Clamp(x + r + 1, 0, w - 1), rem = Math.Clamp(x - r, 0, w - 1);
                sum += s[row + add] - s[row + rem];
            }
        }
    }

    private static void BoxV(byte[] s, byte[] d, int w, int h, int r)
    {
        int win = 2 * r + 1;
        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int k = -r; k <= r; k++) sum += s[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                d[y * w + x] = (byte)(sum / win);
                int add = Math.Clamp(y + r + 1, 0, h - 1), rem = Math.Clamp(y - r, 0, h - 1);
                sum += s[add * w + x] - s[rem * w + x];
            }
        }
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

    /// <summary>
    /// Global colour-range selection: every pixel within <paramref name="tolerance"/> of the seed
    /// colour (and not fully transparent), regardless of contiguity. (Magic Wand without flood-fill.)
    /// </summary>
    public static byte[] ColorRange(byte[] px, int w, int h, byte r, byte g, byte b, int tolerance = 32)
    {
        var m = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int j = i * 4;
            if (px[j + 3] > 0 &&
                Math.Abs(px[j] - r) <= tolerance &&
                Math.Abs(px[j + 1] - g) <= tolerance &&
                Math.Abs(px[j + 2] - b) <= tolerance)
                m[i] = 255;
        }
        return m;
    }

    /// <summary>A fully-selected mask (Select All).</summary>
    public static byte[] Full(int w, int h)
    {
        var m = new byte[w * h];
        Array.Fill(m, (byte)255);
        return m;
    }

    /// <summary>Translate a coverage mask by (dx,dy) document px (zero-fill); for moving a selection.</summary>
    public static byte[] Shift(byte[] mask, int w, int h, int dx, int dy)
    {
        var o = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = y - dy;
            if (sy < 0 || sy >= h) continue;
            for (int x = 0; x < w; x++)
            {
                int sx = x - dx;
                if (sx < 0 || sx >= w) continue;
                o[y * w + x] = mask[sy * w + sx];
            }
        }
        return o;
    }

    /// <summary>Invert coverage (255 − value) per pixel.</summary>
    public static byte[] Invert(byte[] mask)
    {
        var r = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++) r[i] = (byte)(255 - mask[i]);
        return r;
    }

    /// <summary>Grow (dilate) the selection by <paramref name="radius"/> px (separable max).</summary>
    public static byte[] Grow(byte[] mask, int w, int h, int radius)
    {
        if (radius <= 0 || w <= 0 || h <= 0) return mask;
        var t = new byte[w * h]; var o = new byte[w * h];
        MaxH(mask, t, w, h, radius); MaxV(t, o, w, h, radius);
        return o;
    }

    /// <summary>Shrink (erode) the selection by <paramref name="radius"/> px (separable min).</summary>
    public static byte[] Shrink(byte[] mask, int w, int h, int radius)
    {
        if (radius <= 0 || w <= 0 || h <= 0) return mask;
        var t = new byte[w * h]; var o = new byte[w * h];
        MinH(mask, t, w, h, radius); MinV(t, o, w, h, radius);
        return o;
    }

    /// <summary>Smooth (round) the selection edges: box-blur then re-threshold.</summary>
    public static byte[] Smooth(byte[] mask, int w, int h, int radius)
    {
        if (radius <= 0) return mask;
        var f = Feather(mask, w, h, radius);
        var o = new byte[mask.Length];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)(f[i] >= 128 ? 255 : 0);
        return o;
    }

    /// <summary>Border: a band of <paramref name="radius"/> px around the selection edge (grow − shrink).</summary>
    public static byte[] Border(byte[] mask, int w, int h, int radius)
    {
        if (radius <= 0) return mask;
        var g = Grow(mask, w, h, radius);
        var s = Shrink(mask, w, h, radius);
        var o = new byte[mask.Length];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)Math.Clamp(g[i] - s[i], 0, 255);
        return o;
    }

    private static void MaxH(byte[] s, byte[] d, int w, int h, int r)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int m = 0;
                for (int k = -r; k <= r; k++) { int v = s[row + Math.Clamp(x + k, 0, w - 1)]; if (v > m) m = v; }
                d[row + x] = (byte)m;
            }
        }
    }
    private static void MaxV(byte[] s, byte[] d, int w, int h, int r)
    {
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                int m = 0;
                for (int k = -r; k <= r; k++) { int v = s[Math.Clamp(y + k, 0, h - 1) * w + x]; if (v > m) m = v; }
                d[y * w + x] = (byte)m;
            }
    }
    private static void MinH(byte[] s, byte[] d, int w, int h, int r)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int m = 255;
                for (int k = -r; k <= r; k++) { int v = s[row + Math.Clamp(x + k, 0, w - 1)]; if (v < m) m = v; }
                d[row + x] = (byte)m;
            }
        }
    }
    private static void MinV(byte[] s, byte[] d, int w, int h, int r)
    {
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                int m = 255;
                for (int k = -r; k <= r; k++) { int v = s[Math.Clamp(y + k, 0, h - 1) * w + x]; if (v < m) m = v; }
                d[y * w + x] = (byte)m;
            }
    }
}

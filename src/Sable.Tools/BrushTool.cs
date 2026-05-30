namespace Sable.Tools;

/// <summary>Retouch mode: transforms existing pixels under the dab instead of painting colour.</summary>
public enum BrushMode { Paint, Dodge, Burn, Sponge, Blur, Sharpen, Smudge }

/// <summary>
/// Destructive round brush that paints into any RGBA8 buffer (straight alpha,
/// src-over) — a layer's pixels or its mask. Soft circular falloff; strokes
/// interpolate stamps so fast moves don't gap. Also does retouch modes (dodge/burn/
/// sponge/blur/sharpen/smudge) that modify the pixels under the dab. Caller marks dirty.
/// </summary>
public sealed class BrushTool
{
    /// <summary>Retouch mode (Paint = normal colour brush).</summary>
    public BrushMode Mode { get; set; } = BrushMode.Paint;
    /// <summary>Effect amount per dab for retouch modes (0..1).</summary>
    public float Strength { get; set; } = 0.5f;
    private float _smR, _smG, _smB;     // smudge carried colour
    private bool _smInit;

    /// <summary>Reset per-stroke state (smudge carry). Call at the start of each gesture.</summary>
    public void BeginStroke() => _smInit = false;

    public float Radius { get; set; } = 16f;
    public float Hardness { get; set; } = 0.5f;   // 0 = very soft, 1 = hard edge
    public byte R { get; set; } = 255;
    public byte G { get; set; } = 255;
    public byte B { get; set; } = 255;
    public float Flow { get; set; } = 1f;          // max alpha per stamp

    /// <summary>When true the brush erases (destination-out) instead of painting.</summary>
    public bool Erase { get; set; }

    /// <summary>Transparency lock: paint colour but preserve each pixel's existing alpha (PLAN §16.3).</summary>
    public bool LockAlpha { get; set; }

    /// <summary>Pencil: hard aliased edge (no soft falloff / antialiasing).</summary>
    public bool Pencil { get; set; }

    /// <summary>
    /// Document position of the target buffer's (0,0). When the layer buffer has independent
    /// bounds (offset / sub-document), this maps buffer pixel (x,y) → doc pixel (x+OriginX, y+OriginY)
    /// so the doc-space <see cref="Clip"/> rect and <see cref="ClipMask"/> still line up. 0 = aligned.
    /// </summary>
    public int OriginX { get; set; }
    public int OriginY { get; set; }

    /// <summary>Optional clip rect (doc px) — stamps only inside it (selection). Null = unclipped.</summary>
    public (int X, int Y, int W, int H)? Clip { get; set; }

    /// <summary>Optional per-pixel selection mask (doc-sized, 255 = paintable). Null = rect/none.</summary>
    public byte[]? ClipMask { get; set; }
    /// <summary>Row stride of <see cref="ClipMask"/> (doc width).</summary>
    public int ClipMaskW { get; set; }

    // --- clone stamp: sample colour from a source buffer at a locked offset ---
    public bool Clone { get; set; }
    public byte[]? CloneSrc { get; set; }
    public int CloneSrcW { get; set; }
    public int CloneSrcH { get; set; }
    public int CloneOffX { get; set; }   // source pixel = dest - (CloneOffX, CloneOffY)
    public int CloneOffY { get; set; }

    /// <summary>Stamp a single dab centered at (cx, cy) into an RGBA8 buffer.</summary>
    public void Stamp(byte[] px, int w, int h, double cx, double cy)
    {
        float r = Radius;
        int x0 = Math.Max(0, (int)Math.Floor(cx - r));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + r));
        int y0 = Math.Max(0, (int)Math.Floor(cy - r));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + r));
        if (x1 < x0 || y1 < y0) return;

        float inner = r * Math.Clamp(Hardness, 0f, 0.99f);
        float sr = R / 255f, sg = G / 255f, sb = B / 255f;

        // smudge carries the colour under the brush along the stroke
        if (Mode == BrushMode.Smudge)
        {
            int scx = Math.Clamp((int)cx, 0, w - 1), scy = Math.Clamp((int)cy, 0, h - 1);
            int sc = (scy * w + scx) * 4;
            if (!_smInit) { _smR = px[sc]; _smG = px[sc + 1]; _smB = px[sc + 2]; _smInit = true; }
        }

        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int docx = x + OriginX, docy = y + OriginY;   // doc-space coord for selection clip
            if (Clip is { } cl && (docx < cl.X || docy < cl.Y || docx >= cl.X + cl.W || docy >= cl.Y + cl.H)) continue;
            float clipCov = 1f;
            if (ClipMask is { } cm)
            {
                if (docx < 0 || docy < 0 || docx >= ClipMaskW) continue;
                int mi = docy * ClipMaskW + docx;
                if (mi < 0 || mi >= cm.Length || cm[mi] == 0) continue;
                clipCov = cm[mi] / 255f;   // soft (feathered) selection edge
            }
            float dx = (float)(x - cx), dy = (float)(y - cy);
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > r) continue;

            // coverage: 1 inside `inner`, smooth falloff to 0 at `r` (pencil = hard binary edge)
            float cov;
            if (Pencil) cov = dist <= r ? 1f : 0f;
            else
            {
                float t = dist <= inner ? 1f : 1f - (dist - inner) / MathF.Max(1e-3f, r - inner);
                cov = Math.Clamp(t, 0f, 1f);
                cov = cov * cov * (3f - 2f * cov);     // smoothstep
            }

            // retouch modes: transform the existing pixel under the dab
            if (Mode != BrushMode.Paint)
            {
                float amt = Math.Clamp(cov * clipCov * Strength, 0f, 1f);
                if (amt > 0f) Retouch(px, w, h, x, y, amt);
                continue;
            }

            float sa = cov * Flow * clipCov;
            if (sa <= 0f) continue;

            // clone: source colour sampled at the locked offset (skip outside source / transparent)
            float csr = sr, csg = sg, csb = sb;
            if (Clone && CloneSrc is { } cs)
            {
                int srcx = x - CloneOffX, srcy = y - CloneOffY;
                if (srcx < 0 || srcy < 0 || srcx >= CloneSrcW || srcy >= CloneSrcH) continue;
                int sj = (srcy * CloneSrcW + srcx) * 4;
                csr = cs[sj] / 255f; csg = cs[sj + 1] / 255f; csb = cs[sj + 2] / 255f;
                sa *= cs[sj + 3] / 255f;
                if (sa <= 0f) continue;
            }

            int i = (y * w + x) * 4;
            float dr = px[i] / 255f, dg = px[i + 1] / 255f, db = px[i + 2] / 255f, da = px[i + 3] / 255f;

            if (LockAlpha)
            {
                // transparency lock: tint colour toward the brush, keep alpha (no paint on empty pixels)
                if (da <= 0f) continue;
                px[i]     = (byte)(Math.Clamp(dr + (csr - dr) * sa, 0f, 1f) * 255f + 0.5f);
                px[i + 1] = (byte)(Math.Clamp(dg + (csg - dg) * sa, 0f, 1f) * 255f + 0.5f);
                px[i + 2] = (byte)(Math.Clamp(db + (csb - db) * sa, 0f, 1f) * 255f + 0.5f);
                continue;
            }

            if (Erase)
            {
                // destination-out: reduce alpha by coverage, keep color
                float ea = da * (1f - sa);
                px[i + 3] = (byte)(Math.Clamp(ea, 0f, 1f) * 255f + 0.5f);
                continue;
            }

            float outA = sa + da * (1f - sa);
            if (outA <= 0f) { px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0; continue; }
            float outR = (csr * sa + dr * da * (1f - sa)) / outA;
            float outG = (csg * sa + dg * da * (1f - sa)) / outA;
            float outB = (csb * sa + db * da * (1f - sa)) / outA;
            px[i] = (byte)(Math.Clamp(outR, 0f, 1f) * 255f + 0.5f);
            px[i + 1] = (byte)(Math.Clamp(outG, 0f, 1f) * 255f + 0.5f);
            px[i + 2] = (byte)(Math.Clamp(outB, 0f, 1f) * 255f + 0.5f);
            px[i + 3] = (byte)(Math.Clamp(outA, 0f, 1f) * 255f + 0.5f);
        }
    }

    private void Retouch(byte[] px, int w, int h, int x, int y, float amt)
    {
        int i = (y * w + x) * 4;
        float dr = px[i], dg = px[i + 1], db = px[i + 2];
        float nr = dr, ng = dg, nb = db;
        switch (Mode)
        {
            case BrushMode.Dodge:
                nr = dr + (255f - dr) * amt; ng = dg + (255f - dg) * amt; nb = db + (255f - db) * amt; break;
            case BrushMode.Burn:
                nr = dr * (1f - amt); ng = dg * (1f - amt); nb = db * (1f - amt); break;
            case BrushMode.Sponge:   // desaturate toward luminance
            {
                float lum = 0.299f * dr + 0.587f * dg + 0.114f * db;
                nr = dr + (lum - dr) * amt; ng = dg + (lum - dg) * amt; nb = db + (lum - db) * amt; break;
            }
            case BrushMode.Blur:
            {
                var (ar, ag, ab) = Avg3(px, w, h, x, y);
                nr = dr + (ar - dr) * amt; ng = dg + (ag - dg) * amt; nb = db + (ab - db) * amt; break;
            }
            case BrushMode.Sharpen:
            {
                var (ar, ag, ab) = Avg3(px, w, h, x, y);
                nr = dr + (dr - ar) * amt; ng = dg + (dg - ag) * amt; nb = db + (db - ab) * amt; break;
            }
            case BrushMode.Smudge:
                nr = dr + (_smR - dr) * amt; ng = dg + (_smG - dg) * amt; nb = db + (_smB - db) * amt;
                _smR += (dr - _smR) * amt * 0.5f; _smG += (dg - _smG) * amt * 0.5f; _smB += (db - _smB) * amt * 0.5f;
                break;
        }
        px[i] = (byte)Math.Clamp(nr + 0.5f, 0f, 255f);
        px[i + 1] = (byte)Math.Clamp(ng + 0.5f, 0f, 255f);
        px[i + 2] = (byte)Math.Clamp(nb + 0.5f, 0f, 255f);
    }

    private static (float r, float g, float b) Avg3(byte[] px, int w, int h, int cx, int cy)
    {
        float r = 0, g = 0, b = 0; int n = 0;
        for (int yy = cy - 1; yy <= cy + 1; yy++)
        for (int xx = cx - 1; xx <= cx + 1; xx++)
        {
            int sx = Math.Clamp(xx, 0, w - 1), sy = Math.Clamp(yy, 0, h - 1);
            int i = (sy * w + sx) * 4;
            r += px[i]; g += px[i + 1]; b += px[i + 2]; n++;
        }
        return (r / n, g / n, b / n);
    }

    /// <summary>Paint a stroke from (x0,y0) to (x1,y1) into an RGBA8 buffer, interpolating stamps.</summary>
    public void Stroke(byte[] px, int w, int h, double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double spacing = Math.Max(1.0, Radius * 0.25);
        int steps = (int)(dist / spacing);
        for (int s = 0; s <= steps; s++)
        {
            double f = steps == 0 ? 0 : (double)s / steps;
            Stamp(px, w, h, x0 + dx * f, y0 + dy * f);
        }
    }
}

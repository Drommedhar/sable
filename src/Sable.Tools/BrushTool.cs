namespace Sable.Tools;

/// <summary>
/// Destructive round brush that paints into any RGBA8 buffer (straight alpha,
/// src-over) — a layer's pixels or its mask. Soft circular falloff; strokes
/// interpolate stamps so fast moves don't gap. The caller marks the target dirty.
/// </summary>
public sealed class BrushTool
{
    public float Radius { get; set; } = 16f;
    public float Hardness { get; set; } = 0.5f;   // 0 = very soft, 1 = hard edge
    public byte R { get; set; } = 255;
    public byte G { get; set; } = 255;
    public byte B { get; set; } = 255;
    public float Flow { get; set; } = 1f;          // max alpha per stamp

    /// <summary>When true the brush erases (destination-out) instead of painting.</summary>
    public bool Erase { get; set; }

    /// <summary>Optional clip rect (doc px) — stamps only inside it (selection). Null = unclipped.</summary>
    public (int X, int Y, int W, int H)? Clip { get; set; }

    /// <summary>Optional per-pixel selection mask (doc-sized, 255 = paintable). Null = rect/none.</summary>
    public byte[]? ClipMask { get; set; }
    /// <summary>Row stride of <see cref="ClipMask"/> (doc width).</summary>
    public int ClipMaskW { get; set; }

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

        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (Clip is { } cl && (x < cl.X || y < cl.Y || x >= cl.X + cl.W || y >= cl.Y + cl.H)) continue;
            float clipCov = 1f;
            if (ClipMask is { } cm)
            {
                int mi = y * ClipMaskW + x;
                if (mi < 0 || mi >= cm.Length || cm[mi] == 0) continue;
                clipCov = cm[mi] / 255f;   // soft (feathered) selection edge
            }
            float dx = (float)(x - cx), dy = (float)(y - cy);
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > r) continue;

            // coverage: 1 inside `inner`, smooth falloff to 0 at `r`
            float t = dist <= inner ? 1f : 1f - (dist - inner) / MathF.Max(1e-3f, r - inner);
            float cov = Math.Clamp(t, 0f, 1f);
            cov = cov * cov * (3f - 2f * cov);     // smoothstep
            float sa = cov * Flow * clipCov;
            if (sa <= 0f) continue;

            int i = (y * w + x) * 4;
            float dr = px[i] / 255f, dg = px[i + 1] / 255f, db = px[i + 2] / 255f, da = px[i + 3] / 255f;

            if (Erase)
            {
                // destination-out: reduce alpha by coverage, keep color
                float ea = da * (1f - sa);
                px[i + 3] = (byte)(Math.Clamp(ea, 0f, 1f) * 255f + 0.5f);
                continue;
            }

            float outA = sa + da * (1f - sa);
            if (outA <= 0f) { px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0; continue; }
            float outR = (sr * sa + dr * da * (1f - sa)) / outA;
            float outG = (sg * sa + dg * da * (1f - sa)) / outA;
            float outB = (sb * sa + db * da * (1f - sa)) / outA;
            px[i] = (byte)(Math.Clamp(outR, 0f, 1f) * 255f + 0.5f);
            px[i + 1] = (byte)(Math.Clamp(outG, 0f, 1f) * 255f + 0.5f);
            px[i + 2] = (byte)(Math.Clamp(outB, 0f, 1f) * 255f + 0.5f);
            px[i + 3] = (byte)(Math.Clamp(outA, 0f, 1f) * 255f + 0.5f);
        }
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

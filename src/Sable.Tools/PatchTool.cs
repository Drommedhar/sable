namespace Sable.Tools;

/// <summary>
/// Patch (content-aware-ish heal): replace the selected region of a raster with the content
/// from the SAME-shaped region offset by (offX,offY), shifting the source tone to match the
/// destination's own mean tone (so a copy from a brighter/darker area blends in).
///
/// Pure + in-place. The result depends ONLY on <paramref name="src"/> (the immutable
/// gesture-start snapshot) + the offset — the destination bbox is reset from the snapshot
/// first, so repeated calls with the same offset are idempotent and a live drag never
/// compounds/smears. The caller keeps <paramref name="src"/> a fixed clone for the gesture.
///
/// Coordinates are document pixels; the layer buffer's top-left sits at (ox,oy) in the
/// document. The source samples from (x+offX, y+offY) — i.e. dragging the cursor toward the
/// content you want to copy. Out-of-buffer source pixels keep the reset (original) value.
/// </summary>
public static class PatchTool
{
    public static void Apply(byte[] target, byte[] src, int lw, int lh, int ox, int oy,
        (int x, int y, int w, int h)? rect, byte[]? mask, int docW, int offX, int offY)
    {
        if (mask is null && rect is null) return;

        int bx0, by0, bx1, by1;
        if (rect is { } r) { bx0 = r.x; by0 = r.y; bx1 = r.x + r.w; by1 = r.y + r.h; }
        else { bx0 = 0; by0 = 0; bx1 = docW; by1 = lh + oy; }   // mask-only: span the whole mask height
        int cx0 = System.Math.Max(bx0, ox), cy0 = System.Math.Max(by0, oy);
        int cx1 = System.Math.Min(bx1, ox + lw), cy1 = System.Math.Min(by1, oy + lh);
        if (cx1 <= cx0 || cy1 <= cy0) return;

        // reset the bbox to the snapshot so the result is a pure function of (src, offset)
        for (int y = cy0; y < cy1; y++)
        {
            int row = (y - oy) * lw * 4;
            for (int x = cx0; x < cx1; x++)
            {
                int i = row + (x - ox) * 4;
                target[i] = src[i]; target[i + 1] = src[i + 1]; target[i + 2] = src[i + 2]; target[i + 3] = src[i + 3];
            }
        }
        if (offX == 0 && offY == 0) return;

        float Cov(int x, int y)
        {
            if (mask is { } m) { int mi = y * docW + x; return (mi >= 0 && mi < m.Length) ? m[mi] / 255f : 0f; }
            return (rect is { } r2 && x >= r2.x && y >= r2.y && x < r2.x + r2.w && y < r2.y + r2.h) ? 1f : 0f;
        }

        // tone shift = mean(dest) - mean(source) over the covered selection (both read from the snapshot)
        double sdr = 0, sdg = 0, sdb = 0, ssr = 0, ssg = 0, ssb = 0; int cnt = 0;
        for (int y = cy0; y < cy1; y++)
        for (int x = cx0; x < cx1; x++)
        {
            if (Cov(x, y) <= 0f) continue;
            int lsx = x + offX - ox, lsy = y + offY - oy;
            if (lsx < 0 || lsy < 0 || lsx >= lw || lsy >= lh) continue;
            int di = ((y - oy) * lw + (x - ox)) * 4, sj = (lsy * lw + lsx) * 4;
            sdr += src[di]; sdg += src[di + 1]; sdb += src[di + 2];
            ssr += src[sj]; ssg += src[sj + 1]; ssb += src[sj + 2]; cnt++;
        }
        if (cnt == 0) return;
        float tor = (float)((sdr - ssr) / cnt), tog = (float)((sdg - ssg) / cnt), tob = (float)((sdb - ssb) / cnt);

        for (int y = cy0; y < cy1; y++)
        for (int x = cx0; x < cx1; x++)
        {
            float cov = Cov(x, y);
            if (cov <= 0f) continue;
            int lsx = x + offX - ox, lsy = y + offY - oy;
            if (lsx < 0 || lsy < 0 || lsx >= lw || lsy >= lh) continue;
            int di = ((y - oy) * lw + (x - ox)) * 4, sj = (lsy * lw + lsx) * 4;
            float hr = System.Math.Clamp(src[sj] + tor, 0, 255);
            float hg = System.Math.Clamp(src[sj + 1] + tog, 0, 255);
            float hb = System.Math.Clamp(src[sj + 2] + tob, 0, 255);
            target[di] = (byte)(src[di] + (hr - src[di]) * cov + 0.5f);
            target[di + 1] = (byte)(src[di + 1] + (hg - src[di + 1]) * cov + 0.5f);
            target[di + 2] = (byte)(src[di + 2] + (hb - src[di + 2]) * cov + 0.5f);
        }
    }
}

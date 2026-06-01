using System.Threading;
using System.Threading.Tasks;
using Sable.Ai.Imaging;
using Sable.Core.Ai;

namespace Sable.Ai.Tiling;

/// <summary>
/// Overlap-tile a large image through an <see cref="IRasterModel"/> and feather-merge the results
/// (PHASE8_AI §3) so big images don't OOM and tiles don't seam. The geometry (tile plan, feather
/// weights, accumulate/finalize) is pure and unit-tested; <see cref="RunAsync"/> wraps it around a
/// model. The scale factor is inferred from the first tile's output, so the same code serves any
/// integer upscaler (ESRGAN x2/x4) or 1:1 raster model (denoise).
/// </summary>
public static class TileInference
{
    public readonly record struct TileRect(int X, int Y, int W, int H);

    /// <summary>Plan overlapping input tiles covering w×h (each ≤ <paramref name="tile"/>, stepping by tile−overlap).</summary>
    public static IReadOnlyList<TileRect> Plan(int w, int h, int tile, int overlap)
    {
        tile = System.Math.Max(1, tile);
        overlap = System.Math.Clamp(overlap, 0, tile - 1);
        int step = System.Math.Max(1, tile - overlap);
        var tiles = new List<TileRect>();
        for (int y = 0; y < h; y += step)
        {
            int th = System.Math.Min(tile, h - y);
            for (int x = 0; x < w; x += step)
            {
                int tw = System.Math.Min(tile, w - x);
                tiles.Add(new TileRect(x, y, tw, th));
                if (x + tw >= w) break;
            }
            if (y + th >= h) break;
        }
        return tiles;
    }

    /// <summary>
    /// Feather weight for a pixel at (lx,ly) in a tile of (tw,th): 1 in the interior, ramping toward
    /// a small floor across the <paramref name="overlap"/> band at each edge so neighbours cross-fade.
    /// </summary>
    public static float Weight(int lx, int ly, int tw, int th, int overlap)
    {
        const float floor = 0.02f;
        return Axis(lx, tw, overlap) * Axis(ly, th, overlap);

        static float Axis(int p, int len, int ov)
        {
            if (ov <= 0) return 1f;
            float dEdge = System.Math.Min(p + 0.5f, len - 0.5f - p);   // distance to nearest tile edge
            float w = System.Math.Clamp(dEdge / ov, 0f, 1f);
            return System.Math.Max(floor, w);
        }
    }

    /// <summary>Accumulate one OUTPUT-space tile (placed at otx,oty) into colour/weight buffers with feathering.</summary>
    public static void Accumulate(
        float[] colAccum, float[] wAccum, int dw, int dh,
        byte[] tileRgba, int otw, int oth, int otx, int oty, int overlapOut)
    {
        for (int ly = 0; ly < oth; ly++)
        {
            int py = oty + ly;
            if (py < 0 || py >= dh) continue;
            for (int lx = 0; lx < otw; lx++)
            {
                int px = otx + lx;
                if (px < 0 || px >= dw) continue;
                float wgt = Weight(lx, ly, otw, oth, overlapOut);
                int s = (ly * otw + lx) * 4, d = (py * dw + px);
                colAccum[d * 4] += tileRgba[s] * wgt;
                colAccum[d * 4 + 1] += tileRgba[s + 1] * wgt;
                colAccum[d * 4 + 2] += tileRgba[s + 2] * wgt;
                colAccum[d * 4 + 3] += tileRgba[s + 3] * wgt;
                wAccum[d] += wgt;
            }
        }
    }

    /// <summary>Normalise the accumulators into a final RGBA8 image.</summary>
    public static byte[] Finalize(float[] colAccum, float[] wAccum, int dw, int dh)
    {
        var outp = new byte[dw * dh * 4];
        for (int i = 0; i < dw * dh; i++)
        {
            float wsum = wAccum[i];
            if (wsum <= 0f) continue;
            for (int c = 0; c < 4; c++)
                outp[i * 4 + c] = (byte)System.Math.Clamp(colAccum[i * 4 + c] / wsum + 0.5f, 0, 255);
        }
        return outp;
    }

    /// <summary>Tile <paramref name="src"/>, run each tile through <paramref name="model"/>, feather-merge.</summary>
    public static async Task<AiImage> RunAsync(
        IRasterModel model, AiImage src, AiParams p,
        int tile = 256, int overlap = 16,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var rects = Plan(src.Width, src.Height, tile, overlap);
        float[]? col = null, wts = null;
        int dw = 0, dh = 0, factor = 1;

        for (int i = 0; i < rects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = rects[i];
            var tileImg = new AiImage(ImageOps.Crop(src.Rgba, src.Width, src.Height, t.X, t.Y, t.W, t.H), t.W, t.H);
            var outTile = await model.ApplyAsync(tileImg, null, p, ct).ConfigureAwait(false);

            if (col is null)
            {
                factor = System.Math.Max(1, outTile.Width / System.Math.Max(1, t.W));
                dw = src.Width * factor; dh = src.Height * factor;
                col = new float[dw * dh * 4];
                wts = new float[dw * dh];
            }
            Accumulate(col, wts!, dw, dh, outTile.Rgba, outTile.Width, outTile.Height, t.X * factor, t.Y * factor, overlap * factor);
            progress?.Report((double)(i + 1) / rects.Count);
        }

        if (col is null) return src;   // empty plan (shouldn't happen)
        return new AiImage(Finalize(col, wts!, dw, dh), dw, dh);
    }
}

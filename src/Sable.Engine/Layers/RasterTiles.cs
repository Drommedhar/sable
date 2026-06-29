using System.Runtime.CompilerServices;

namespace Sable.Engine.Layers;

/// <summary>
/// 256² tile read/write over a flat RGBA buffer (PLAN §4). Edge-aware. Used for
/// dirty-tile undo snapshots of any raster target. The copy ops are generic over the
/// channel element type so the SAME code serves <b>byte[] RGBA8</b> (masks — 1 byte/channel)
/// and <b>float[] RGBA32F</b> (pixel layers — the bit-depth float pipeline, PLAN §6).
/// Stride is 4 channels per pixel; element size comes from <see cref="Unsafe.SizeOf{T}"/>.
/// </summary>
public static class RasterTiles
{
    public const int TileSize = 256;

    public static int TilesX(int width) => (width + TileSize - 1) / TileSize;
    public static int TilesY(int height) => (height + TileSize - 1) / TileSize;
    public static int TileWidth(int width, int tx) => Math.Min(TileSize, width - tx * TileSize);
    public static int TileHeight(int height, int ty) => Math.Min(TileSize, height - ty * TileSize);

    /// <summary>
    /// Copy a sub-rectangle out of an RGBA buffer into a new (cw×ch) buffer (crop/resize).
    /// Source pixels outside the original bounds come out transparent (zeroed). Pure copy,
    /// generic over the channel type (byte mask or float pixels).
    /// </summary>
    public static T[] Crop<T>(T[] src, int srcW, int srcH, int cropX, int cropY, int cw, int ch) where T : struct
    {
        var dst = new T[cw * ch * 4];
        for (int y = 0; y < ch; y++)
        {
            int sy = cropY + y;
            if (sy < 0 || sy >= srcH) continue;
            for (int x = 0; x < cw; x++)
            {
                int sx = cropX + x;
                if (sx < 0 || sx >= srcW) continue;
                int si = (sy * srcW + sx) * 4, di = (y * cw + x) * 4;
                Array.Copy(src, si, dst, di, 4);
            }
        }
        return dst;
    }

    /// <summary>
    /// Resample a byte[] RGBA8 buffer to a new size (mask resize). Bilinear (premultiplied,
    /// so transparent edges don't halo) or nearest-neighbour. Returns a new dst buffer.
    /// </summary>
    public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh, bool bilinear)
    {
        var dst = new byte[dw * dh * 4];
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return dst;
        double fx = (double)sw / dw, fy = (double)sh / dh;

        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int di = (y * dw + x) * 4;
            if (!bilinear)
            {
                int sx = Math.Min(sw - 1, (int)((x + 0.5) * fx));
                int sy = Math.Min(sh - 1, (int)((y + 0.5) * fy));
                int si = (sy * sw + sx) * 4;
                dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3];
                continue;
            }

            double gx = (x + 0.5) * fx - 0.5, gy = (y + 0.5) * fy - 0.5;
            int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
            double tx = gx - x0, ty = gy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = Math.Clamp(x0, 0, sw - 1); x1 = Math.Clamp(x1, 0, sw - 1);
            y0 = Math.Clamp(y0, 0, sh - 1); y1 = Math.Clamp(y1, 0, sh - 1);

            double pr = 0, pg = 0, pb = 0, pa = 0;
            AddSample(src, sw, x0, y0, (1 - tx) * (1 - ty), ref pr, ref pg, ref pb, ref pa);
            AddSample(src, sw, x1, y0, tx * (1 - ty), ref pr, ref pg, ref pb, ref pa);
            AddSample(src, sw, x0, y1, (1 - tx) * ty, ref pr, ref pg, ref pb, ref pa);
            AddSample(src, sw, x1, y1, tx * ty, ref pr, ref pg, ref pb, ref pa);

            byte a = (byte)Math.Clamp(pa * 255.0 + 0.5, 0, 255);
            if (pa > 1e-6)
            {
                dst[di] = (byte)Math.Clamp(pr / pa * 255.0 + 0.5, 0, 255);
                dst[di + 1] = (byte)Math.Clamp(pg / pa * 255.0 + 0.5, 0, 255);
                dst[di + 2] = (byte)Math.Clamp(pb / pa * 255.0 + 0.5, 0, 255);
            }
            dst[di + 3] = a;
        }
        return dst;
    }

    private static void AddSample(byte[] src, int sw, int sx, int sy, double wgt,
        ref double pr, ref double pg, ref double pb, ref double pa)
    {
        int si = (sy * sw + sx) * 4;
        double a = src[si + 3] / 255.0;
        pr += src[si] / 255.0 * a * wgt;
        pg += src[si + 1] / 255.0 * a * wgt;
        pb += src[si + 2] / 255.0 * a * wgt;
        pa += a * wgt;
    }

    /// <summary>
    /// Resample a float[] RGBA32F buffer to a new size (pixel-layer resize, bit-depth pipeline).
    /// Channels are already in working units (0..1 for SDR, &gt;1 for HDR) so no 255 scaling.
    /// Premultiplied bilinear or nearest. HDR values are preserved (no 0..1 clamp).
    /// </summary>
    public static float[] ResampleF(float[] src, int sw, int sh, int dw, int dh, bool bilinear)
    {
        var dst = new float[dw * dh * 4];
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return dst;
        double fx = (double)sw / dw, fy = (double)sh / dh;

        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int di = (y * dw + x) * 4;
            if (!bilinear)
            {
                int sx = Math.Min(sw - 1, (int)((x + 0.5) * fx));
                int sy = Math.Min(sh - 1, (int)((y + 0.5) * fy));
                int si = (sy * sw + sx) * 4;
                dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3];
                continue;
            }

            double gx = (x + 0.5) * fx - 0.5, gy = (y + 0.5) * fy - 0.5;
            int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
            double tx = gx - x0, ty = gy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = Math.Clamp(x0, 0, sw - 1); x1 = Math.Clamp(x1, 0, sw - 1);
            y0 = Math.Clamp(y0, 0, sh - 1); y1 = Math.Clamp(y1, 0, sh - 1);

            double pr = 0, pg = 0, pb = 0, pa = 0;
            AddSampleF(src, sw, x0, y0, (1 - tx) * (1 - ty), ref pr, ref pg, ref pb, ref pa);
            AddSampleF(src, sw, x1, y0, tx * (1 - ty), ref pr, ref pg, ref pb, ref pa);
            AddSampleF(src, sw, x0, y1, (1 - tx) * ty, ref pr, ref pg, ref pb, ref pa);
            AddSampleF(src, sw, x1, y1, tx * ty, ref pr, ref pg, ref pb, ref pa);

            dst[di + 3] = (float)pa;
            if (pa > 1e-9)
            {
                dst[di] = (float)(pr / pa);
                dst[di + 1] = (float)(pg / pa);
                dst[di + 2] = (float)(pb / pa);
            }
        }
        return dst;
    }

    private static void AddSampleF(float[] src, int sw, int sx, int sy, double wgt,
        ref double pr, ref double pg, ref double pb, ref double pa)
    {
        int si = (sy * sw + sx) * 4;
        double a = src[si + 3];
        pr += src[si] * a * wgt;
        pg += src[si + 1] * a * wgt;
        pb += src[si + 2] * a * wgt;
        pa += a * wgt;
    }

    /// <summary>Copy a 256² tile out of an RGBA buffer (byte mask or float pixels) into a new buffer.</summary>
    public static T[] GetTile<T>(T[] px, int width, int height, int tx, int ty) where T : struct
    {
        int tw = TileWidth(width, tx), th = TileHeight(height, ty);
        int es = Unsafe.SizeOf<T>();
        var data = new T[tw * th * 4];
        for (int ry = 0; ry < th; ry++)
        {
            int srcRow = ((ty * TileSize + ry) * width + tx * TileSize) * 4;
            Buffer.BlockCopy(px, srcRow * es, data, ry * tw * 4 * es, tw * 4 * es);
        }
        return data;
    }

    /// <summary>Write a 256² tile back into an RGBA buffer (byte mask or float pixels).</summary>
    public static void SetTile<T>(T[] px, int width, int height, int tx, int ty, T[] data) where T : struct
    {
        int tw = TileWidth(width, tx), th = TileHeight(height, ty);
        int es = Unsafe.SizeOf<T>();
        for (int ry = 0; ry < th; ry++)
        {
            int dstRow = ((ty * TileSize + ry) * width + tx * TileSize) * 4;
            Buffer.BlockCopy(data, ry * tw * 4 * es, px, dstRow * es, tw * 4 * es);
        }
    }
}

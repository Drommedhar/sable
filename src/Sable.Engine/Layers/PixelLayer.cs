namespace Sable.Engine.Layers;

/// <summary>
/// A raster layer. Stores a single full-resolution <b>RGBA32F</b> buffer (straight alpha,
/// channel order R,G,B,A; length = Width*Height*4 floats). This is the document working
/// precision: 0..1 for SDR, values &gt;1 allowed for HDR headroom (bit-depth pipeline, PLAN §6).
/// The numeric encoding matches the old RGBA8 values un-quantized (sRGB-encoded in float) — a
/// precision widening, not a colour-space change. 8/16-bit is an import/export boundary
/// (<see cref="Sable.Engine.Document.Depth"/>); internal storage is always float. The mask
/// stays 8-bit (<see cref="Layer.Mask"/>, R = coverage), matching Photoshop. Tiled 256² access
/// (<see cref="RasterTiles"/>) backs dirty-tile undo snapshots.
/// </summary>
public sealed class PixelLayer : Layer
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>RGBA32F pixels, length = Width * Height * 4 (straight alpha, R,G,B,A).</summary>
    public float[] Pixels { get; private set; }

    public PixelLayer(int width, int height, string name = "Layer")
    {
        Width = width;
        Height = height;
        Pixels = new float[width * height * 4];
        Name = name;
    }

    /// <summary>Layer-local content bounds (the move/select overlay adds OffsetX/Y for doc space).</summary>
    public override (int x, int y, int w, int h) ContentBounds(int docW, int docH) => (0, 0, Width, Height);

    /// <summary>Replace the float pixel buffer and dimensions (e.g. on crop/resize). Undo restores the old buffer.</summary>
    public void SetBuffer(int width, int height, float[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Dirty = true;
        DirtyTiles.Clear();
    }

    /// <summary>Replace the buffer from an RGBA8 source (codec / PSD / AI / clipboard), converting
    /// /255 → linear float. Use when the producer hands back 8-bit straight-alpha bytes.</summary>
    public void SetBufferFromBytes(int width, int height, byte[] rgba8)
        => SetBuffer(width, height, BytesToFloat(rgba8));

    /// <summary>Quantise the float buffer to RGBA8 straight-alpha bytes (×255, round, clamp) — for
    /// 8-bit export / clipboard / eyedropper-display paths. HDR values &gt;1 clamp to 255.</summary>
    public byte[] ToBytes() => FloatToBytes(Pixels);

    /// <summary>RGBA8 (0..255) → RGBA32F (0..1). Uses true division (not ×(1/255)) so the
    /// byte→float→byte round-trip is bit-exact and matches n/255f literals elsewhere.</summary>
    public static float[] BytesToFloat(byte[] src)
    {
        var dst = new float[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] / 255f;
        return dst;
    }

    /// <summary>RGBA32F → RGBA8 (×255, round, clamp 0..255).</summary>
    public static byte[] FloatToBytes(float[] src)
    {
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = (byte)Math.Clamp(src[i] * 255f + 0.5f, 0f, 255f);
        return dst;
    }

    /// <summary>
    /// Grow the buffer (preserving content + adjusting <see cref="Layer.OffsetX"/>/<see cref="Layer.OffsetY"/>)
    /// so it covers the union of its current bounds and the document rect [0,docW)×[0,docH). Used before
    /// painting so a sub-document / offset layer is paintable across the whole canvas without losing the
    /// off-canvas pixels it already holds. Returns true if the buffer changed.
    /// </summary>
    public bool ExpandToCover(int docW, int docH)
    {
        int x0 = Math.Min(0, OffsetX);
        int y0 = Math.Min(0, OffsetY);
        int x1 = Math.Max(docW, OffsetX + Width);
        int y1 = Math.Max(docH, OffsetY + Height);
        int nw = x1 - x0, nh = y1 - y0;
        if (nw == Width && nh == Height) return false;   // already covers the doc

        int dx = OffsetX - x0, dy = OffsetY - y0;        // where the old buffer lands in the new one
        var nbuf = new float[nw * nh * 4];
        for (int y = 0; y < Height; y++)
            Array.Copy(Pixels, y * Width * 4, nbuf, ((y + dy) * nw + dx) * 4, Width * 4);
        if (Mask is { } m)   // the mask is layer-aligned (byte RGBA8) → grow it the same way
        {
            var nmask = new byte[nw * nh * 4];
            for (int y = 0; y < Height; y++)
                Array.Copy(m, y * Width * 4, nmask, ((y + dy) * nw + dx) * 4, Width * 4);
            Mask = nmask; MaskDirty = true;
        }
        Width = nw; Height = nh;
        Pixels = nbuf;
        OffsetX = x0; OffsetY = y0;
        Dirty = true; DirtyTiles.Clear();
        return true;
    }

    /// <summary>
    /// Shrink the buffer to the tight bounding box of non-transparent pixels (adjusting
    /// <see cref="Layer.OffsetX"/>/<see cref="Layer.OffsetY"/> so content stays put in document
    /// space). Keeps a 1×1 buffer if the layer is fully transparent. Returns true if it changed.
    /// The inverse of <see cref="ExpandToCover"/>: paint expands, then this auto-crops to content.
    /// </summary>
    public bool TrimToContent()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width * 4;
            for (int x = 0; x < Width; x++)
            {
                if (Pixels[row + x * 4 + 3] <= 0f) continue;   // transparent
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)   // fully transparent → collapse to 1×1 at the current top-left
        {
            if (Width == 1 && Height == 1) return false;
            Width = Height = 1;
            Pixels = new float[4];
            if (Mask is not null) { Mask = new byte[4]; MaskDirty = true; }
            Dirty = true; DirtyTiles.Clear();
            return true;
        }

        int nw = maxX - minX + 1, nh = maxY - minY + 1;
        if (nw == Width && nh == Height) return false;   // already tight

        var nbuf = new float[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            Array.Copy(Pixels, ((minY + y) * Width + minX) * 4, nbuf, y * nw * 4, nw * 4);
        if (Mask is { } m)
        {
            var nmask = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                Array.Copy(m, ((minY + y) * Width + minX) * 4, nmask, y * nw * 4, nw * 4);
            Mask = nmask; MaskDirty = true;
        }
        Width = nw; Height = nh;
        Pixels = nbuf;
        OffsetX += minX; OffsetY += minY;
        Dirty = true; DirtyTiles.Clear();
        return true;
    }

    protected override Layer CreateClone()
    {
        var c = new PixelLayer(Width, Height, Name);
        Pixels.CopyTo(c.Pixels.AsSpan());
        return c;
    }

    // 256² tile access (PLAN §4) delegates to RasterTiles (generic; float pixels here).
    public float[] GetTile(int tx, int ty) => RasterTiles.GetTile(Pixels, Width, Height, tx, ty);
    public void SetTile(int tx, int ty, float[] data) => RasterTiles.SetTile(Pixels, Width, Height, tx, ty, data);
}

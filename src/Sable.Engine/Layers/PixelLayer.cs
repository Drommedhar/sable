namespace Sable.Engine.Layers;

/// <summary>
/// A raster layer. M1: stores a single full-resolution RGBA8 buffer (straight
/// alpha, byte order R,G,B,A). Tiled 256×256 GPU-resident storage (PLAN §4) is
/// the immediate follow-up — this is the correctness-first stepping stone.
/// </summary>
public sealed class PixelLayer : Layer
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>RGBA8 pixels, length = Width * Height * 4.</summary>
    public byte[] Pixels { get; private set; }

    public PixelLayer(int width, int height, string name = "Layer")
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        Name = name;
    }

    /// <summary>Layer-local content bounds (the move/select overlay adds OffsetX/Y for doc space).</summary>
    public override (int x, int y, int w, int h) ContentBounds(int docW, int docH) => (0, 0, Width, Height);

    /// <summary>Replace the pixel buffer and dimensions (e.g. on crop/resize). Undo restores the old buffer.</summary>
    public void SetBuffer(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Dirty = true;
        DirtyTiles.Clear();
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
        var nbuf = new byte[nw * nh * 4];
        for (int y = 0; y < Height; y++)
            Array.Copy(Pixels, y * Width * 4, nbuf, ((y + dy) * nw + dx) * 4, Width * 4);
        if (Mask is { } m)   // the mask is layer-aligned → grow it the same way
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
                if (Pixels[row + x * 4 + 3] == 0) continue;   // transparent
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
            Pixels = new byte[4];
            if (Mask is not null) { Mask = new byte[4]; MaskDirty = true; }
            Dirty = true; DirtyTiles.Clear();
            return true;
        }

        int nw = maxX - minX + 1, nh = maxY - minY + 1;
        if (nw == Width && nh == Height) return false;   // already tight

        var nbuf = new byte[nw * nh * 4];
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

    // 256² tile access (PLAN §4) delegates to RasterTiles (shared with masks).
    public byte[] GetTile(int tx, int ty) => RasterTiles.GetTile(Pixels, Width, Height, tx, ty);
    public void SetTile(int tx, int ty, byte[] data) => RasterTiles.SetTile(Pixels, Width, Height, tx, ty, data);
}

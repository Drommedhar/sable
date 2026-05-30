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

    /// <summary>Replace the pixel buffer and dimensions (e.g. on crop/resize). Undo restores the old buffer.</summary>
    public void SetBuffer(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Dirty = true;
        DirtyTiles.Clear();
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

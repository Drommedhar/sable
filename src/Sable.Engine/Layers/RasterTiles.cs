namespace Sable.Engine.Layers;

/// <summary>
/// 256² tile read/write over a flat RGBA8 buffer (PLAN §4). Edge-aware. Used for
/// dirty-tile undo snapshots of any raster target — layer pixels or a mask.
/// </summary>
public static class RasterTiles
{
    public const int TileSize = 256;

    public static int TilesX(int width) => (width + TileSize - 1) / TileSize;
    public static int TilesY(int height) => (height + TileSize - 1) / TileSize;
    public static int TileWidth(int width, int tx) => Math.Min(TileSize, width - tx * TileSize);
    public static int TileHeight(int height, int ty) => Math.Min(TileSize, height - ty * TileSize);

    public static byte[] GetTile(byte[] px, int width, int height, int tx, int ty)
    {
        int tw = TileWidth(width, tx), th = TileHeight(height, ty);
        var data = new byte[tw * th * 4];
        for (int ry = 0; ry < th; ry++)
        {
            int srcRow = ((ty * TileSize + ry) * width + tx * TileSize) * 4;
            Buffer.BlockCopy(px, srcRow, data, ry * tw * 4, tw * 4);
        }
        return data;
    }

    public static void SetTile(byte[] px, int width, int height, int tx, int ty, byte[] data)
    {
        int tw = TileWidth(width, tx), th = TileHeight(height, ty);
        for (int ry = 0; ry < th; ry++)
        {
            int dstRow = ((ty * TileSize + ry) * width + tx * TileSize) * 4;
            Buffer.BlockCopy(data, ry * tw * 4, px, dstRow, tw * 4);
        }
    }
}

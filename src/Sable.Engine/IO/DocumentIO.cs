using Sable.Engine.Layers;
using Sable.Imaging;

namespace Sable.Engine.IO;

/// <summary>
/// Document-level import/export. Bridges the raster <see cref="ImageCodec"/> and
/// the engine document model. Native <c>.sable</c> container IO (PLAN §4) lands in
/// Sable.Format; this is the flat-image path.
/// </summary>
public static class DocumentIO
{
    /// <summary>Open an image as a new single-layer document sized to the image.</summary>
    public static Document OpenImage(string path)
    {
        var (w, h, rgba) = ImageCodec.DecodeRgba(path);
        var doc = new Document(w, h);
        var layer = new PixelLayer(w, h, Path.GetFileNameWithoutExtension(path));
        Array.Copy(rgba, layer.Pixels, Math.Min(rgba.Length, layer.Pixels.Length));
        doc.Layers.Add(layer);
        return doc;
    }

    /// <summary>Write a flattened RGBA8 composite to a PNG file.</summary>
    public static void ExportPng(string path, int width, int height, byte[] rgba)
        => ImageCodec.EncodePng(path, width, height, rgba);

    /// <summary>Export the flattened RGBA8 to a file in the chosen format, optionally resized (PLAN §16.12).</summary>
    public static void Export(string path, ImageCodec.ImageFormat fmt, int srcW, int srcH, byte[] rgba, int outW, int outH, int quality)
        => System.IO.File.WriteAllBytes(path, ImageCodec.EncodeScaled(fmt, srcW, srcH, rgba, outW, outH, quality));
}

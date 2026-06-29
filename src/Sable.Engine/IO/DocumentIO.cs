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
    /// <summary>Open an image as a new single-layer document sized to the image. Decodes to RGBA32F and
    /// records the source bit depth on the document (8-bit images stay 8-bit; 16-bit PNG/TIFF import as
    /// 16-bit, keeping their precision — bit-depth pipeline, PLAN §6).</summary>
    public static Document OpenImage(string path)
    {
        var (w, h, rgba, srcBits) = ImageCodec.DecodeFloat(path);
        var doc = new Document(w, h)
        {
            Depth = srcBits switch { 32 => Sable.Core.BitDepth.ThirtyTwo, 16 => Sable.Core.BitDepth.Sixteen, _ => Sable.Core.BitDepth.Eight },
        };
        var layer = new PixelLayer(w, h, Path.GetFileNameWithoutExtension(path));
        layer.SetBuffer(w, h, rgba);
        doc.Layers.Add(layer);
        return doc;
    }

    /// <summary>Write a flattened RGBA8 composite to a PNG file.</summary>
    public static void ExportPng(string path, int width, int height, byte[] rgba)
        => ImageCodec.EncodePng(path, width, height, rgba);

    /// <summary>Export the flattened RGBA8 to a file in the chosen format, optionally resized (PLAN §16.12).
    /// <paramref name="dpi"/> &gt; 0 writes the physical resolution (PNG pHYs / JPEG JFIF density).</summary>
    public static void Export(string path, ImageCodec.ImageFormat fmt, int srcW, int srcH, byte[] rgba, int outW, int outH, int quality,
        double dpi = 0, byte[]? icc = null, string? iccName = null)
        => System.IO.File.WriteAllBytes(path,
            ImageMeta.ApplyDpi(ImageCodec.EncodeScaled(fmt, srcW, srcH, rgba, outW, outH, quality, icc, iccName), fmt, dpi));

    /// <summary>Export the flattened RGBA32F composite at the document's bit depth — PNG/TIFF write a true
    /// 16-bit file when <paramref name="depthBits"/> ≥ 16; everything else quantises to 8-bit (bit-depth
    /// pipeline, PLAN §6).</summary>
    public static void ExportFloat(string path, ImageCodec.ImageFormat fmt, int srcW, int srcH, float[] rgba, int outW, int outH,
        int quality, int depthBits, double dpi = 0, byte[]? icc = null, string? iccName = null)
        => System.IO.File.WriteAllBytes(path,
            ImageMeta.ApplyDpi(ImageCodec.EncodeScaledFloat(fmt, srcW, srcH, rgba, outW, outH, quality, depthBits, icc, iccName), fmt, dpi));
}

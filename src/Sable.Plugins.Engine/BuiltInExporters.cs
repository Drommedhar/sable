using Sable.Imaging;
using Sable.Plugin.Sdk.Export;
using Sable.Plugins;

namespace Sable.Plugins.Engine;

/// <summary>An <see cref="IExportProvider"/> over a built-in <see cref="ImageCodec.ImageFormat"/>.
/// The host scales/flattens before calling, so Encode just runs the codec at the given size.</summary>
public sealed class BuiltInExporter : IExportProvider
{
    private readonly ImageCodec.ImageFormat _fmt;

    public BuiltInExporter(string id, string label, ImageCodec.ImageFormat fmt, bool supportsAlpha)
    {
        Id = id; Label = label; _fmt = fmt; SupportsAlpha = supportsAlpha;
        Extension = ImageCodec.Extension(fmt);
    }

    public string Id { get; }
    public string Label { get; }
    public string Extension { get; }
    public bool SupportsAlpha { get; }

    public byte[] Encode(ExportImage image, ExportOptions options)
        => ImageCodec.EncodeScaled(_fmt, image.Width, image.Height, image.Rgba,
            image.Width, image.Height, options.Quality, options.IccProfile, options.IccProfileName);
}

/// <summary>Registers Sable's built-in formats into an <see cref="ExportRegistry"/> so they sit in
/// the same list as plugin-contributed formats (the export UI reads the registry).</summary>
public static class BuiltInExporters
{
    public static void RegisterAll(ExportRegistry registry)
    {
        registry.Register(new BuiltInExporter("png", "PNG", ImageCodec.ImageFormat.Png, supportsAlpha: true));
        registry.Register(new BuiltInExporter("jpeg", "JPEG", ImageCodec.ImageFormat.Jpeg, supportsAlpha: false));
        registry.Register(new BuiltInExporter("webp", "WebP", ImageCodec.ImageFormat.Webp, supportsAlpha: true));
        registry.Register(new BuiltInExporter("tiff", "TIFF", ImageCodec.ImageFormat.Tiff, supportsAlpha: true));
    }
}

namespace Sable.Plugin.Sdk.Pixels;

/// <summary>A raw RGBA8 pixel buffer (straight alpha, row-major, length = Width*Height*4).</summary>
public sealed record PixelBuffer
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }
}

/// <summary>
/// Read access to pixels (capability <c>pixel.read</c>). Null on
/// <see cref="Host.IHostContext.Pixels"/> when not granted. Buffers are copies — mutating them
/// does not affect the document (writing back is a separate capability).
/// </summary>
public interface IPixelApi
{
    /// <summary>The active layer's pixels at its own size/offset, or null (no layer / not a pixel layer).</summary>
    PixelBuffer? ActiveLayer();

    /// <summary>The flattened document composite (doc-sized), or null when unavailable headlessly.</summary>
    PixelBuffer? Composite();
}

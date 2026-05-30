using System.Runtime.InteropServices;
using SkiaSharp;

namespace Sable.Imaging;

/// <summary>
/// Raster codec IO (PLAN §2.3). Decodes/encodes via SkiaSharp (MIT). Works in
/// straight-alpha RGBA8 (byte order R,G,B,A) — the engine's pixel-layer format.
/// Returns raw pixels only; building a Document from them lives in the engine to
/// keep Imaging free of an Engine dependency.
/// </summary>
public static class ImageCodec
{
    /// <summary>Decode any Skia-supported image (PNG/JPEG/WebP/…) to RGBA8.</summary>
    public static (int width, int height, byte[] rgba) DecodeRgba(string path)
    {
        using var input = SKBitmap.Decode(path)
            ?? throw new IOException($"Could not decode image: {path}");

        var info = new SKImageInfo(input.Width, input.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(input, 0, 0);
        }
        return (input.Width, input.Height, bmp.Bytes);
    }

    /// <summary>Encode RGBA8 pixels to a PNG file.</summary>
    public static void EncodePng(string path, int width, int height, byte[] rgba)
    {
        using var fs = File.Create(path);
        fs.Write(EncodePngBytes(width, height, rgba));
    }

    /// <summary>Encode RGBA8 pixels to PNG bytes (for the OS clipboard).</summary>
    public static byte[] EncodePngBytes(int width, int height, byte[] rgba)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        Marshal.Copy(rgba, 0, bmp.GetPixels(), Math.Min(rgba.Length, width * height * 4));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Decode image bytes (PNG/JPEG/…) to RGBA8, or null if undecodable (OS clipboard paste).</summary>
    public static (int width, int height, byte[] rgba)? DecodeRgbaBytes(byte[] bytes)
    {
        using var input = SKBitmap.Decode(bytes);
        if (input is null) return null;
        var info = new SKImageInfo(input.Width, input.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(input, 0, 0);
        }
        return (input.Width, input.Height, bmp.Bytes);
    }
}

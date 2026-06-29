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
    /// <summary>Decode any Skia-supported image (PNG/JPEG/WebP/…) to RGBA8, honouring the EXIF
    /// orientation tag (camera photos come in upright).</summary>
    public static (int width, int height, byte[] rgba) DecodeRgba(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is not null && SKBitmap.Decode(codec) is { } viaCodec)
            using (viaCodec) return DecodeOriented(viaCodec, codec.EncodedOrigin);

        using var input = SKBitmap.Decode(path)
            ?? throw new IOException($"Could not decode image: {path}");
        return DecodeOriented(input, SKEncodedOrigin.TopLeft);
    }

    /// <summary>Convert a decoded bitmap to straight-alpha RGBA8 in sRGB (Skia converts from the
    /// file's embedded ICC profile when it tagged the decode), applying the EXIF origin transform.</summary>
    private static (int width, int height, byte[] rgba) DecodeOriented(SKBitmap input, SKEncodedOrigin origin)
    {
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? input.Height : input.Width;
        int h = swap ? input.Width : input.Height;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:    canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
                case SKEncodedOrigin.BottomLeft:  canvas.Translate(0, h); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.LeftTop:     canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.RightTop:    canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom: canvas.Translate(w, h); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.LeftBottom:  canvas.Translate(0, h); canvas.RotateDegrees(-90); break;
            }
            canvas.DrawBitmap(input, 0, 0);
        }
        return (w, h, bmp.Bytes);
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

    /// <summary>Export raster formats SkiaSharp can encode (PLAN §16.12). TIFF uses a self-contained
    /// baseline encoder (uncompressed RGBA8) since Skia has no TIFF writer.</summary>
    public enum ImageFormat { Png, Jpeg, Webp, Tiff }

    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => "jpg",
        ImageFormat.Webp => "webp",
        ImageFormat.Tiff => "tif",
        _ => "png",
    };

    /// <summary>Pick the best supported format for a file extension (default = PNG).</summary>
    public static ImageFormat FormatFromExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" ? ImageFormat.Jpeg
            : ext is ".webp" ? ImageFormat.Webp
            : ext is ".tif" or ".tiff" ? ImageFormat.Tiff
            : ImageFormat.Png;
    }

    /// <summary>Encode RGBA8 to bytes in the given format, optionally resized. quality 1..100 (PNG ignores it).
    /// JPEG has no alpha → flattened over white.</summary>
    public static byte[] EncodeScaled(ImageFormat fmt, int srcW, int srcH, byte[] rgba, int outW, int outH, int quality)
    {
        var info = new SKImageInfo(srcW, srcH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var srcBmp = new SKBitmap(info);
        Marshal.Copy(rgba, 0, srcBmp.GetPixels(), Math.Min(rgba.Length, srcW * srcH * 4));

        SKBitmap work = srcBmp;
        SKBitmap? resized = null;
        if (outW != srcW || outH != srcH)
        {
            resized = srcBmp.Resize(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                                    SKSamplingOptions.Default);
            work = resized ?? srcBmp;
        }

        SKBitmap? flat = null;
        if (fmt == ImageFormat.Jpeg)   // JPEG: no alpha — composite over white
        {
            flat = new SKBitmap(new SKImageInfo(work.Width, work.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (var c = new SKCanvas(flat)) { c.Clear(SKColors.White); c.DrawBitmap(work, 0, 0); }
            work = flat;
        }

        // TIFF: Skia has no TIFF writer — use the self-contained baseline encoder (uncompressed RGBA8).
        if (fmt == ImageFormat.Tiff)
        {
            var tiffBytes = EncodeTiff(work.Width, work.Height, GetPixels(work));
            resized?.Dispose();
            flat?.Dispose();
            return tiffBytes;
        }

        var skfmt = fmt switch
        {
            ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            ImageFormat.Webp => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        using var img = SKImage.FromBitmap(work);
        using var data = img.Encode(skfmt, Math.Clamp(quality, 1, 100));
        var bytes = data.ToArray();
        resized?.Dispose();
        flat?.Dispose();
        return bytes;
    }

    /// <summary>Read the RGBA8 pixel bytes out of an SKBitmap (premul → straight not needed; we
    /// build the bitmap with Unpremul alpha).</summary>
    private static byte[] GetPixels(SKBitmap bmp)
    {
        var px = new byte[bmp.Width * bmp.Height * 4];
        Marshal.Copy(bmp.GetPixels(), px, 0, Math.Min(px.Length, bmp.Width * bmp.Height * 4));
        return px;
    }

    /// <summary>Encode RGBA8 pixels as a baseline uncompressed TIFF (little-endian, RGBA, no compression).
    /// Widely readable by Photoshop/Affinity/GIMP/ImageMagick. No external dependency — pure byte
    /// assembly of the TIFF IFD + strip data (roadmap Workstream 6: TIFF export).</summary>
    public static byte[] EncodeTiff(int width, int height, byte[] rgba)
    {
        // Layout: 8-byte header + IFD (12 entries) + out-of-line arrays + strip pixel data.
        // Out-of-line: BitsPerSample (4 shorts), SampleFormat (4 shorts), XRes + YRes (2 rationals).
        const int ifdEntries = 12;
        int headerLen = 8;
        int ifdLen = 2 + ifdEntries * 12 + 4;          // entry-count + entries + next-IFD (0)
        int bpsOffset = headerLen + ifdLen;             // 4 shorts (8 bytes)
        int sfOffset = bpsOffset + 8;                   // 4 shorts (8 bytes)
        int resOffset = sfOffset + 8;                   // 2 rationals = 4 longs (16 bytes)
        int stripOffset = resOffset + 16;
        int stripBytes = width * height * 4;

        var ms = new System.IO.MemoryStream(stripOffset + stripBytes);
        void W16(ushort v) { ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)(v >> 8)); }
        void W32(uint v) { ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)((v >> 8) & 0xFF)); ms.WriteByte((byte)((v >> 16) & 0xFF)); ms.WriteByte((byte)((v >> 24) & 0xFF)); }
        void Entry(ushort tag, ushort type, uint count, uint value)
        { W16(tag); W16(type); W32(count); W32(value); }

        // ---- header (little-endian) ----
        ms.WriteByte((byte)'I'); ms.WriteByte((byte)'I');   // byte order: little-endian
        W16(42);            // TIFF magic
        W32(8);             // offset to first IFD

        // ---- IFD (entries must be sorted by tag) ----
        W16(ifdEntries);
        Entry(256, 3, 1, (uint)width);                 // ImageWidth (SHORT)
        Entry(257, 3, 1, (uint)height);                // ImageLength (SHORT)
        Entry(258, 3, 4, (uint)bpsOffset);             // BitsPerSample → 8,8,8,8
        Entry(259, 3, 1, 1);                           // Compression: 1 = none
        Entry(262, 3, 1, 2);                           // PhotometricInterpretation: 2 = RGB
        Entry(273, 4, 1, (uint)stripOffset);           // StripOffsets (LONG)
        Entry(277, 3, 1, 4);                           // SamplesPerPixel: 4 (RGBA)
        Entry(278, 3, 1, (uint)height);                // RowsPerStrip: whole image
        Entry(279, 4, 1, (uint)stripBytes);            // StripByteCounts (LONG)
        Entry(282, 5, 1, (uint)resOffset);             // XResolution (RATIONAL → 72/1)
        Entry(283, 5, 1, (uint)(resOffset + 8));       // YResolution (RATIONAL → 72/1)
        Entry(339, 3, 4, (uint)sfOffset);              // SampleFormat → 1,1,1,2 (uint,uint,uint,alpha)
        W32(0);                                         // next IFD = 0

        // ---- out-of-line arrays ----
        ms.Position = bpsOffset; W16(8); W16(8); W16(8); W16(8);           // BitsPerSample
        ms.Position = sfOffset; W16(1); W16(1); W16(1); W16(2);            // SampleFormat
        ms.Position = resOffset; W32(72); W32(1); W32(72); W32(1);         // XRes 72/1, YRes 72/1

        // ---- strip pixel data (RGBA8, top-down — TIFF stores top row first) ----
        ms.Position = stripOffset;
        ms.Write(rgba, 0, Math.Min(rgba.Length, stripBytes));
        return ms.ToArray();
    }

    /// <summary>Decode image bytes (PNG/JPEG/…) to RGBA8 sRGB, or null if undecodable (OS clipboard paste).</summary>
    public static (int width, int height, byte[] rgba)? DecodeRgbaBytes(byte[] bytes)
    {
        using var input = SKBitmap.Decode(bytes);
        if (input is null) return null;
        return DecodeOriented(input, SKEncodedOrigin.TopLeft);
    }
}

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

    /// <summary>Decode any Skia-supported image to straight-alpha <b>RGBA32F</b> (sRGB-encoded, 0..1),
    /// reporting the SOURCE bits-per-channel (8 / 16, or 32 for float sources) so the document can record
    /// its working depth. 16-bit PNG/TIFF keep their precision instead of being quantised to 8-bit on
    /// import (bit-depth pipeline, PLAN §6). EXIF orientation honoured.</summary>
    public static (int width, int height, float[] rgba, int srcBits) DecodeFloat(string path)
    {
        // Skia's libpng strips 16-bit PNGs to 8-bit on decode, so read true 16-bit PNGs ourselves first.
        var bytes = File.ReadAllBytes(path);
        if (TryDecodePng16(bytes) is { } hi) return (hi.width, hi.height, hi.rgba, 16);

        using var codec = SKCodec.Create(path);
        int srcBits = 8;
        var origin = SKEncodedOrigin.TopLeft;
        SKBitmap? input = null;
        if (codec is not null)
        {
            srcBits = BitsFromColorType(codec.Info.ColorType);
            origin = codec.EncodedOrigin;
            // Decode straight into a 16-bit bitmap so 16-bit sources keep their precision — the codec
            // normalises Info to 8-bit, so we must explicitly request a high-precision target.
            var hiInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba16161616,
                                         SKAlphaType.Unpremul, codec.Info.ColorSpace ?? SKColorSpace.CreateSrgb());
            var bm = new SKBitmap();
            if (bm.TryAllocPixels(hiInfo)
                && codec.GetPixels(hiInfo, bm.GetPixels()) is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                input = bm;
            else { bm.Dispose(); input = SKBitmap.Decode(codec); }
        }
        input ??= SKBitmap.Decode(path) ?? throw new IOException($"Could not decode image: {path}");
        using (input)
        {
            var (w, h, rgba) = DecodeOrientedFloat(input, origin);
            return (w, h, rgba, srcBits);
        }
    }

    private static int BitsFromColorType(SKColorType ct) => ct switch
    {
        SKColorType.RgbaF32 => 32,
        SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped or SKColorType.Rgba16161616 => 16,
        _ => 8,
    };

    /// <summary>As <see cref="DecodeOriented"/> but produces an RGBA32F buffer (4 floats/px, 0..1) — Skia
    /// converts the source (8/16-bit or float) into float, applying its ICC profile and the EXIF origin.</summary>
    private static (int width, int height, float[] rgba) DecodeOrientedFloat(SKBitmap input, SKEncodedOrigin origin)
    {
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? input.Height : input.Width;
        int h = swap ? input.Width : input.Height;
        var info = new SKImageInfo(w, h, SKColorType.RgbaF32, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
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
        var floats = MemoryMarshal.Cast<byte, float>(bmp.Bytes).ToArray();
        return (w, h, floats);
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
    /// JPEG has no alpha → flattened over white. <paramref name="icc"/> = embedded ICC profile to write
    /// (PNG iCCP / TIFF tag 34675); JPEG/WebP ICC embedding is a follow-up.</summary>
    public static byte[] EncodeScaled(ImageFormat fmt, int srcW, int srcH, byte[] rgba, int outW, int outH, int quality,
        byte[]? icc = null, string? iccName = null)
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
            var tiffBytes = EncodeTiff(work.Width, work.Height, GetPixels(work), icc);
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
        if (fmt == ImageFormat.Png && icc is { Length: > 0 })
            bytes = InjectPngIccp(bytes, icc, iccName ?? "ICC profile");
        return bytes;
    }

    /// <summary>Encode RGBA32F pixels to bytes at the document depth (bit-depth pipeline, PLAN §6).
    /// <paramref name="depthBits"/> ≥ 16 with PNG/TIFF writes a true 16-bit file (HDR &gt;1 clamps to 1);
    /// everything else (8-bit, or JPEG/WebP which have no 16-bit) quantises to RGBA8 and reuses
    /// <see cref="EncodeScaled"/>. Optionally resized.</summary>
    public static byte[] EncodeScaledFloat(ImageFormat fmt, int srcW, int srcH, float[] rgba, int outW, int outH,
        int quality, int depthBits, byte[]? icc = null, string? iccName = null)
    {
        bool wantHi = depthBits >= 16 && (fmt == ImageFormat.Png || fmt == ImageFormat.Tiff);
        if (!wantHi)
        {
            var b8 = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i++) b8[i] = (byte)Math.Clamp(rgba[i] * 255f + 0.5f, 0f, 255f);
            return EncodeScaled(fmt, srcW, srcH, b8, outW, outH, quality, icc, iccName);
        }

        // 16-bit: pack into a Rgba16161616 bitmap (native LE u16/channel on our platforms)
        var info = new SKImageInfo(srcW, srcH, SKColorType.Rgba16161616, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        using var srcBmp = new SKBitmap(info);
        var u16 = new byte[srcW * srcH * 4 * 2];
        for (int i = 0; i < rgba.Length; i++)
        {
            ushort v = (ushort)Math.Clamp(rgba[i] * 65535f + 0.5f, 0f, 65535f);
            u16[i * 2] = (byte)(v & 0xFF); u16[i * 2 + 1] = (byte)(v >> 8);
        }
        Marshal.Copy(u16, 0, srcBmp.GetPixels(), u16.Length);

        SKBitmap work = srcBmp;
        SKBitmap? resized = null;
        if (outW != srcW || outH != srcH)
        {
            resized = srcBmp.Resize(new SKImageInfo(outW, outH, SKColorType.Rgba16161616, SKAlphaType.Unpremul,
                                    SKColorSpace.CreateSrgb()), SKSamplingOptions.Default);
            work = resized ?? srcBmp;
        }

        // read the (resized) 16-bit pixels back as native LE u16/channel
        var outU16 = new byte[work.Width * work.Height * 4 * 2];
        Marshal.Copy(work.GetPixels(), outU16, 0, outU16.Length);
        // Self-contained encoders (Skia's simple Encode returns null for 16-bit color types).
        byte[] result = fmt == ImageFormat.Tiff
            ? EncodeTiff(work.Width, work.Height, outU16, icc, 16)
            : EncodePng16(work.Width, work.Height, outU16, icc, iccName);
        resized?.Dispose();
        return result;
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
    public static byte[] EncodeTiff(int width, int height, byte[] rgba, byte[]? icc = null, int bits = 8)
    {
        // Layout: 8-byte header + IFD + out-of-line arrays + (optional) ICC + strip pixel data.
        // Out-of-line: BitsPerSample (4 shorts), SampleFormat (4 shorts), XRes + YRes (2 rationals).
        // <paramref name="bits"/> = 8 or 16 per channel; for 16-bit, <paramref name="rgba"/> holds
        // little-endian ushort channels (length = w*h*4*2).
        int sampleBytes = bits / 8;
        bool hasIcc = icc is { Length: > 0 };
        int ifdEntries = hasIcc ? 13 : 12;
        int headerLen = 8;
        int ifdLen = 2 + ifdEntries * 12 + 4;          // entry-count + entries + next-IFD (0)
        int bpsOffset = headerLen + ifdLen;             // 4 shorts (8 bytes)
        int sfOffset = bpsOffset + 8;                   // 4 shorts (8 bytes)
        int resOffset = sfOffset + 8;                   // 2 rationals = 4 longs (16 bytes)
        int iccOffset = resOffset + 16;                 // ICC profile blob (when present)
        int iccLen = hasIcc ? icc!.Length : 0;
        int stripOffset = iccOffset + iccLen + (iccLen & 1);   // keep the strip on an even offset
        int stripBytes = width * height * 4 * sampleBytes;

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
        W16((ushort)ifdEntries);
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
        if (hasIcc) Entry(34675, 7, (uint)iccLen, (uint)iccOffset);   // ICCProfile (UNDEFINED) — sorts last
        W32(0);                                         // next IFD = 0

        // ---- out-of-line arrays ----
        ms.Position = bpsOffset; W16((ushort)bits); W16((ushort)bits); W16((ushort)bits); W16((ushort)bits);   // BitsPerSample
        ms.Position = sfOffset; W16(1); W16(1); W16(1); W16(2);            // SampleFormat
        ms.Position = resOffset; W32(72); W32(1); W32(72); W32(1);         // XRes 72/1, YRes 72/1
        if (hasIcc) { ms.Position = iccOffset; ms.Write(icc!, 0, iccLen); }

        // ---- strip pixel data (RGBA8, top-down — TIFF stores top row first) ----
        ms.Position = stripOffset;
        ms.Write(rgba, 0, Math.Min(rgba.Length, stripBytes));
        return ms.ToArray();
    }

    /// <summary>Encode a true 16-bit RGBA PNG (bit depth 16, colour type 6) from native little-endian
    /// ushort channels — Skia's simple <c>Encode</c> can't write 16-bit, so this is a self-contained
    /// encoder (IHDR + single IDAT of filter-0 scanlines, PNG stores 16-bit samples big-endian).</summary>
    public static byte[] EncodePng16(int width, int height, byte[] rgbaU16le, byte[]? icc = null, string? iccName = null)
    {
        int rowBytes = width * 4 * 2;
        var raw = new byte[height * (1 + rowBytes)];   // each row: 1 filter byte + RGBA u16 (big-endian)
        for (int y = 0; y < height; y++)
        {
            int ro = y * (1 + rowBytes), src = y * rowBytes;
            raw[ro] = 0;   // filter type 0 = none
            for (int s = 0; s < width * 4; s++)
            {
                raw[ro + 1 + s * 2] = rgbaU16le[src + s * 2 + 1];   // high byte first (BE)
                raw[ro + 1 + s * 2 + 1] = rgbaU16le[src + s * 2];
            }
        }

        byte[] idat;
        using (var z = new System.IO.MemoryStream())
        {
            using (var def = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true))
                def.Write(raw, 0, raw.Length);
            idat = z.ToArray();
        }

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);   // PNG signature
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width); WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 16; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // 16-bit, RGBA, deflate, no filter/interlace
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        var png = ms.ToArray();
        return icc is { Length: > 0 } ? InjectPngIccp(png, icc, iccName ?? "ICC profile") : png;
    }

    /// <summary>Decode a 16-bit truecolour (RGB/RGBA) or greyscale PNG to RGBA32F (0..1), or null if it
    /// isn't a 16-bit non-interlaced PNG (8-bit / palette / interlaced fall back to Skia). Inflates IDAT,
    /// applies the 5 PNG filter types, and reads big-endian 16-bit samples. Self-contained — Skia's
    /// libpng strips 16-bit PNGs to 8-bit, so this is the only path that preserves their precision.</summary>
    private static (int width, int height, float[] rgba)? TryDecodePng16(byte[] f)
    {
        if (f.Length < 8 || f[0] != 137 || f[1] != 80 || f[2] != 78 || f[3] != 71) return null;
        int pos = 8, w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
        bool sawIhdr = false;
        using var idat = new System.IO.MemoryStream();
        while (pos + 8 <= f.Length)
        {
            uint len = Be(f, pos); pos += 4;
            string type = System.Text.Encoding.ASCII.GetString(f, pos, 4); pos += 4;
            if (pos + (long)len + 4 > f.Length) break;
            if (type == "IHDR") { w = (int)Be(f, pos); h = (int)Be(f, pos + 4); bitDepth = f[pos + 8]; colorType = f[pos + 9]; interlace = f[pos + 12]; sawIhdr = true; }
            else if (type == "IDAT") idat.Write(f, pos, (int)len);
            else if (type == "IEND") break;
            pos += (int)len + 4;   // chunk data + CRC
        }
        if (!sawIhdr || bitDepth != 16 || interlace != 0) return null;
        int channels = colorType switch { 2 => 3, 6 => 4, 0 => 1, 4 => 2, _ => 0 };
        if (channels == 0 || w <= 0 || h <= 0 || (long)w * h * channels * 2 > 512_000_000) return null;

        int bpp = channels * 2, rowBytes = w * bpp;
        var raw = new byte[h * (1 + rowBytes)];
        idat.Position = 0;
        try
        {
            using var zs = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
            int read = 0, n;
            while (read < raw.Length && (n = zs.Read(raw, read, raw.Length - read)) > 0) read += n;
            if (read < raw.Length) return null;   // truncated / unexpected
        }
        catch { return null; }

        var img = new byte[h * rowBytes];   // unfiltered scanlines
        for (int y = 0; y < h; y++)
        {
            int fo = y * (1 + rowBytes), co = y * rowBytes, po = (y - 1) * rowBytes;
            byte filter = raw[fo];
            for (int x = 0; x < rowBytes; x++)
            {
                int a = x >= bpp ? img[co + x - bpp] : 0;
                int b = y > 0 ? img[po + x] : 0;
                int c = (y > 0 && x >= bpp) ? img[po + x - bpp] : 0;
                int v = raw[fo + 1 + x];
                v += filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
                img[co + x] = (byte)v;
            }
        }

        var outp = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int s = i * bpp;
            float r, g, bl, al;
            if (channels >= 3) { r = Sample16(img, s); g = Sample16(img, s + 2); bl = Sample16(img, s + 4); al = channels == 4 ? Sample16(img, s + 6) : 1f; }
            else { r = g = bl = Sample16(img, s); al = channels == 2 ? Sample16(img, s + 2) : 1f; }
            outp[i * 4] = r; outp[i * 4 + 1] = g; outp[i * 4 + 2] = bl; outp[i * 4 + 3] = al;
        }
        return (w, h, outp);
    }

    private static float Sample16(byte[] d, int o) => ((d[o] << 8) | d[o + 1]) / 65535f;
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    private static uint Be(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    private static void WriteChunk(System.IO.Stream s, string type, byte[] data)
    {
        var head = new byte[4]; WriteBE(head, 0, (uint)data.Length); s.Write(head, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var body = new byte[typeBytes.Length + data.Length];
        Array.Copy(typeBytes, body, typeBytes.Length);
        Array.Copy(data, 0, body, typeBytes.Length, data.Length);
        s.Write(body, 0, body.Length);
        var crc = new byte[4]; WriteBE(crc, 0, Crc32(body, 0, body.Length)); s.Write(crc, 0, 4);
    }

    /// <summary>Insert an <c>iCCP</c> chunk (embedded ICC profile, zlib-compressed) into a PNG byte
    /// stream, right after the IHDR chunk. Returns the PNG unchanged when <paramref name="icc"/> is
    /// empty or the input isn't a recognisable PNG.</summary>
    public static byte[] InjectPngIccp(byte[] png, byte[]? icc, string profileName = "ICC profile")
    {
        if (icc is not { Length: > 0 } || png.Length < 8 + 12 + 13) return png;
        // PNG sig (8) then IHDR chunk = len(4)+"IHDR"(4)+data(13)+crc(4) = 8 + 25.
        int ihdrEnd = 8 + 4 + 4 + 13 + 4;
        if (png[8 + 4] != 'I' || png[8 + 5] != 'H' || png[8 + 6] != 'D' || png[8 + 7] != 'R') return png;

        // iCCP data: name (Latin-1, ≤79) + 0x00 + compression(0) + zlib(profile)
        string name = string.IsNullOrEmpty(profileName) ? "ICC profile" : profileName;
        if (name.Length > 79) name = name[..79];
        byte[] nameBytes = System.Text.Encoding.Latin1.GetBytes(name);
        byte[] zicc;
        using (var z = new System.IO.MemoryStream())
        {
            using (var def = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true))
                def.Write(icc, 0, icc.Length);
            zicc = z.ToArray();
        }
        var data = new byte[nameBytes.Length + 2 + zicc.Length];
        Array.Copy(nameBytes, data, nameBytes.Length);
        data[nameBytes.Length] = 0;       // null terminator
        data[nameBytes.Length + 1] = 0;   // compression method: 0 = zlib
        Array.Copy(zicc, 0, data, nameBytes.Length + 2, zicc.Length);

        var chunk = new byte[12 + data.Length];
        WriteBE(chunk, 0, (uint)data.Length);
        chunk[4] = (byte)'i'; chunk[5] = (byte)'C'; chunk[6] = (byte)'C'; chunk[7] = (byte)'P';
        Array.Copy(data, 0, chunk, 8, data.Length);
        WriteBE(chunk, 8 + data.Length, Crc32(chunk, 4, 4 + data.Length));   // CRC over type+data

        var outp = new byte[png.Length + chunk.Length];
        Array.Copy(png, 0, outp, 0, ihdrEnd);                       // sig + IHDR
        Array.Copy(chunk, 0, outp, ihdrEnd, chunk.Length);          // iCCP
        Array.Copy(png, ihdrEnd, outp, ihdrEnd + chunk.Length, png.Length - ihdrEnd);
        return outp;
    }

    private static void WriteBE(byte[] b, int o, uint v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static uint[]? _crcTable;
    private static uint Crc32(byte[] data, int start, int end)
    {
        var t = _crcTable ??= BuildCrcTable();
        uint c = 0xFFFFFFFF;
        for (int i = start; i < end; i++) c = t[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>Decode image bytes (PNG/JPEG/…) to RGBA8 sRGB, or null if undecodable (OS clipboard paste).</summary>
    public static (int width, int height, byte[] rgba)? DecodeRgbaBytes(byte[] bytes)
    {
        using var input = SKBitmap.Decode(bytes);
        if (input is null) return null;
        return DecodeOriented(input, SKEncodedOrigin.TopLeft);
    }
}

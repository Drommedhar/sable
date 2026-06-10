namespace Sable.Imaging;

/// <summary>
/// Post-encode metadata patching SkiaSharp can't do itself: physical resolution (DPI) in
/// PNG (pHYs chunk) and JPEG (JFIF density). WebP has no standard DPI field — passed through.
/// Pure byte manipulation, no dependencies.
/// </summary>
public static class ImageMeta
{
    /// <summary>Return the encoded image with the given DPI written into its metadata.
    /// Unknown/duplicate-safe: returns the input unchanged when the format carries no DPI.</summary>
    public static byte[] ApplyDpi(byte[] encoded, ImageCodec.ImageFormat fmt, double dpi)
    {
        if (dpi <= 0) return encoded;
        return fmt switch
        {
            ImageCodec.ImageFormat.Png => PngWithDpi(encoded, dpi),
            ImageCodec.ImageFormat.Jpeg => JpegWithDpi(encoded, dpi),
            _ => encoded,
        };
    }

    // --- PNG: insert a pHYs chunk (pixels per metre) right after IHDR ---

    private static byte[] PngWithDpi(byte[] png, double dpi)
    {
        // signature (8) + IHDR (4 len + 4 type + 13 data + 4 crc) = 33; bail on anything unexpected
        if (png.Length < 33 || png[0] != 0x89 || png[1] != (byte)'P') return png;

        uint ppm = (uint)System.Math.Round(dpi / 0.0254);   // dots/inch → pixels/metre
        var data = new byte[9];
        WriteBe(data, 0, ppm);
        WriteBe(data, 4, ppm);
        data[8] = 1;   // unit: metre

        var chunk = new byte[4 + 4 + 9 + 4];
        WriteBe(chunk, 0, 9);
        chunk[4] = (byte)'p'; chunk[5] = (byte)'H'; chunk[6] = (byte)'Y'; chunk[7] = (byte)'s';
        System.Array.Copy(data, 0, chunk, 8, 9);
        WriteBe(chunk, 17, Crc32(chunk, 4, 13));   // crc over type + data

        var result = new byte[png.Length + chunk.Length];
        System.Array.Copy(png, 0, result, 0, 33);
        System.Array.Copy(chunk, 0, result, 33, chunk.Length);
        System.Array.Copy(png, 33, result, 33 + chunk.Length, png.Length - 33);
        return result;
    }

    private static void WriteBe(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] buf, int off, int len)
    {
        if (_crcTable is null)
        {
            _crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                _crcTable[n] = c;
            }
        }
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < len; i++) crc = _crcTable[(crc ^ buf[off + i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    // --- JPEG: patch (or insert) the JFIF APP0 density fields ---

    private static byte[] JpegWithDpi(byte[] jpg, double dpi)
    {
        if (jpg.Length < 4 || jpg[0] != 0xFF || jpg[1] != 0xD8) return jpg;
        ushort den = (ushort)System.Math.Clamp(System.Math.Round(dpi), 1, 65535);

        // existing JFIF APP0 right after SOI → patch units + densities in place
        if (jpg.Length >= 18 && jpg[2] == 0xFF && jpg[3] == 0xE0 &&
            jpg[6] == (byte)'J' && jpg[7] == (byte)'F' && jpg[8] == (byte)'I' && jpg[9] == (byte)'F' && jpg[10] == 0)
        {
            var patched = (byte[])jpg.Clone();
            patched[13] = 1;                      // units: dots per inch
            patched[14] = (byte)(den >> 8); patched[15] = (byte)den;
            patched[16] = (byte)(den >> 8); patched[17] = (byte)den;
            return patched;
        }

        // no JFIF header → insert a minimal APP0 after SOI
        var app0 = new byte[18];
        app0[0] = 0xFF; app0[1] = 0xE0;
        app0[2] = 0; app0[3] = 16;                // segment length (excludes the marker)
        app0[4] = (byte)'J'; app0[5] = (byte)'F'; app0[6] = (byte)'I'; app0[7] = (byte)'F'; app0[8] = 0;
        app0[9] = 1; app0[10] = 1;                // JFIF 1.1
        app0[11] = 1;                             // units: dpi
        app0[12] = (byte)(den >> 8); app0[13] = (byte)den;
        app0[14] = (byte)(den >> 8); app0[15] = (byte)den;
        // app0[16..17] = 0: no thumbnail

        var result = new byte[jpg.Length + app0.Length];
        result[0] = jpg[0]; result[1] = jpg[1];
        System.Array.Copy(app0, 0, result, 2, app0.Length);
        System.Array.Copy(jpg, 2, result, 2 + app0.Length, jpg.Length - 2);
        return result;
    }
}

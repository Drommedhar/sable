using System.IO;
using Sable.Imaging;
using Xunit;

namespace Sable.Tests;

/// <summary>
/// Export format tests (roadmap Workstream 6): TIFF encoder + JPEG/WebP/TIFF encode-via-Skia
/// round-trip. TIFF uses the self-contained baseline encoder; JPEG/WebP go through Skia and are
/// decoded back to verify dimensions. Pure logic — no GPU.
/// </summary>
public class ExportFormatTests
{
    private static byte[] MakeRgba(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
        return px;
    }

    // ---- TIFF baseline encoder ----

    [Fact]
    public void Tiff_Header_IsLittleEndianWithValidMagic()
    {
        var tiff = ImageCodec.EncodeTiff(2, 2, MakeRgba(2, 2, 255, 0, 0));
        Assert.Equal((byte)'I', tiff[0]);
        Assert.Equal((byte)'I', tiff[1]);
        Assert.Equal(42, tiff[2]);              // TIFF magic (little-endian)
        Assert.Equal(0, tiff[3]);
        Assert.Equal(8u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4)));
    }

    [Fact]
    public void Tiff_EncodesDimensionsAndPixels()
    {
        int w = 3, h = 2;
        var rgba = MakeRgba(w, h, 10, 20, 30);
        var tiff = ImageCodec.EncodeTiff(w, h, rgba);
        // Verify the IFD dimensions + strip pixel data directly (Skia's TIFF decoder is limited;
        // the baseline TIFF targets Photoshop/Affinity/GIMP, not Skia re-decode).
        Assert.True(tiff.Length >= 8 + 2 + 12 * 12 + 4 + 8 + 8 + 16 + w * h * 4);
        // ImageWidth (tag 256) at IFD entry 0: tag(2) + type(2) + count(4) + value(4) → value at offset 8+2+8
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(8 + 2 + 8));
        Assert.Equal((uint)w, width);
        // strip data starts after header+IFD+arrays; first pixel should be the RGBA we wrote
        int stripOffset = 8 + 2 + 12 * 12 + 4 + 8 + 8 + 16;
        Assert.Equal(10, tiff[stripOffset]);
        Assert.Equal(20, tiff[stripOffset + 1]);
        Assert.Equal(30, tiff[stripOffset + 2]);
    }

    [Fact]
    public void Tiff_PreservesAlpha()
    {
        int w = 2, h = 2;
        var rgba = MakeRgba(w, h, 255, 0, 0, 128);
        var tiff = ImageCodec.EncodeTiff(w, h, rgba);
        int stripOffset = 8 + 2 + 12 * 12 + 4 + 8 + 8 + 16;
        Assert.Equal(128, tiff[stripOffset + 3]);   // alpha channel of first pixel
    }

    // ---- ImageFormat enum + extension mapping ----

    [Theory]
    [InlineData(ImageCodec.ImageFormat.Png, "png")]
    [InlineData(ImageCodec.ImageFormat.Jpeg, "jpg")]
    [InlineData(ImageCodec.ImageFormat.Webp, "webp")]
    [InlineData(ImageCodec.ImageFormat.Tiff, "tif")]
    public void Extension_MapsEachFormat(ImageCodec.ImageFormat fmt, string expected)
        => Assert.Equal(expected, ImageCodec.Extension(fmt));

    [Theory]
    [InlineData("photo.png", ImageCodec.ImageFormat.Png)]
    [InlineData("photo.jpg", ImageCodec.ImageFormat.Jpeg)]
    [InlineData("photo.JPEG", ImageCodec.ImageFormat.Jpeg)]
    [InlineData("photo.webp", ImageCodec.ImageFormat.Webp)]
    [InlineData("photo.tif", ImageCodec.ImageFormat.Tiff)]
    [InlineData("photo.tiff", ImageCodec.ImageFormat.Tiff)]
    [InlineData("photo.unknown", ImageCodec.ImageFormat.Png)]
    public void FormatFromExtension_PicksCorrectFormat(string path, ImageCodec.ImageFormat expected)
        => Assert.Equal(expected, ImageCodec.FormatFromExtension(path));

    // ---- EncodeScaled round-trip (JPEG/WebP via Skia, TIFF via baseline encoder) ----

    [Fact]
    public void EncodeScaled_Jpeg_FlattensOverWhiteAndDecodes()
    {
        int w = 4, h = 4;
        var rgba = MakeRgba(w, h, 0, 0, 255, 0);   // transparent blue → should flatten to white
        var jpg = ImageCodec.EncodeScaled(ImageCodec.ImageFormat.Jpeg, w, h, rgba, w, h, 90);
        var decoded = ImageCodec.DecodeRgbaBytes(jpg);
        Assert.NotNull(decoded);
        Assert.Equal(w, decoded!.Value.width);
        // JPEG is lossy but the flattened white background should be near-white
        Assert.True(decoded.Value.rgba[0] > 200, "transparent area flattened to white");
    }

    [Fact]
    public void EncodeScaled_Tiff_RoundTripsViaStructure()
    {
        int w = 3, h = 3;
        var rgba = MakeRgba(w, h, 200, 100, 50);
        var tiff = ImageCodec.EncodeScaled(ImageCodec.ImageFormat.Tiff, w, h, rgba, w, h, 100);
        // Verify via byte structure (Skia can't decode baseline TIFF — see Tiff_EncodesDimensionsAndPixels).
        Assert.True(tiff.Length >= 8 + 2 + 12 * 12 + 4 + 8 + 8 + 16 + w * h * 4);
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(8 + 2 + 8));
        Assert.Equal((uint)w, width);
    }

    [Fact]
    public void EncodeScaled_Tiff_Resizes()
    {
        var rgba = MakeRgba(4, 4, 200, 100, 50);
        var tiff = ImageCodec.EncodeScaled(ImageCodec.ImageFormat.Tiff, 4, 4, rgba, 8, 8, 100);
        // Resized to 8×8 → IFD width = 8, strip = 8*8*4 bytes.
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(8 + 2 + 8));
        Assert.Equal(8u, width);
        int stripOffset = 8 + 2 + 12 * 12 + 4 + 8 + 8 + 16;
        Assert.Equal(8 * 8 * 4, tiff.Length - stripOffset);
    }

    // ---- 16-bit export (bit-depth pipeline, PLAN §6) ----

    [Fact]
    public void EncodeScaledFloat_Png16_PreservesSubBytePrecision()
    {
        // a value strictly BETWEEN two 8-bit steps (100.6/255) can only survive at >8-bit precision.
        int w = 4, h = 2;
        float val = 100.6f / 255f;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++) { rgba[i * 4] = val; rgba[i * 4 + 1] = val; rgba[i * 4 + 2] = val; rgba[i * 4 + 3] = 1f; }

        var png = ImageCodec.EncodeScaledFloat(ImageCodec.ImageFormat.Png, w, h, rgba, w, h, 100, depthBits: 16);
        var path = Path.Combine(Path.GetTempPath(), $"sable16_{System.Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        try
        {
            var (dw, dh, dec, bits) = ImageCodec.DecodeFloat(path);
            Assert.Equal(w, dw);
            Assert.Equal(h, dh);
            float err = System.Math.Abs(dec[0] - val);
            // 8-bit would snap to 100/255 or 101/255 → err ≈ 0.6/255; 16-bit keeps it far tighter.
            Assert.True(err < 0.5f / 255f,
                $"precision lost: val={val * 65535f:F1}/65535 decoded={dec[0] * 65535f:F1}/65535 (={dec[0] * 255f:F2}/255) srcBits={bits} pngBitDepth={png[24]} err={err * 255f:F3} steps");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EncodeScaledFloat_Tiff16_WritesBitsPerSample16AndDoubleStrip()
    {
        int w = 3, h = 3;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = 0.5f;
        var tiff = ImageCodec.EncodeScaledFloat(ImageCodec.ImageFormat.Tiff, w, h, rgba, w, h, 100, depthBits: 16);

        // BitsPerSample out-of-line array sits at headerLen + ifdLen = 8 + (2 + 12*12 + 4) = 158.
        int bpsOffset = 8 + (2 + 12 * 12 + 4);
        Assert.Equal(16, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(bpsOffset)));
        // 16-bit strip is 2 bytes/channel.
        int stripOffset = bpsOffset + 8 + 8 + 16;
        Assert.Equal(w * h * 4 * 2, tiff.Length - stripOffset);
    }

    [Fact]
    public void EncodeScaledFloat_8Bit_QuantisesToOrdinaryFile()
    {
        // depthBits 8 → the 8-bit path: a normal RGBA8 PNG that decodes back.
        int w = 4, h = 4;
        var rgba = new float[w * h * 4];
        for (int i = 0; i < w * h; i++) { rgba[i * 4] = 200 / 255f; rgba[i * 4 + 3] = 1f; }
        var png = ImageCodec.EncodeScaledFloat(ImageCodec.ImageFormat.Png, w, h, rgba, w, h, 100, depthBits: 8);
        var dec = ImageCodec.DecodeRgbaBytes(png);
        Assert.NotNull(dec);
        Assert.Equal(w, dec!.Value.width);
        Assert.InRange(dec.Value.rgba[0], 198, 202);
    }
}

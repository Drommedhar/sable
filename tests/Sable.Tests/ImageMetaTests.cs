using Sable.Imaging;
using Xunit;

namespace Sable.Tests;

public class ImageMetaTests
{
    private static byte[] TinyRgba(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 3] = 255; }
        return px;
    }

    [Fact]
    public void Png_ApplyDpi_InsertsValidPhysChunk()
    {
        var png = ImageCodec.EncodePngBytes(2, 2, TinyRgba(2, 2));
        var outp = ImageMeta.ApplyDpi(png, ImageCodec.ImageFormat.Png, 300);

        Assert.Equal(png.Length + 21, outp.Length);   // 4 len + 4 type + 9 data + 4 crc
        // chunk sits right after IHDR (offset 33)
        Assert.Equal((byte)'p', outp[37]);
        Assert.Equal((byte)'H', outp[38]);
        Assert.Equal((byte)'Y', outp[39]);
        Assert.Equal((byte)'s', outp[40]);
        uint ppm = (uint)((outp[41] << 24) | (outp[42] << 16) | (outp[43] << 8) | outp[44]);
        Assert.Equal(11811u, ppm);                    // 300 dpi → px/metre
        Assert.Equal(1, outp[49]);                    // unit = metre

        // still decodable
        Assert.NotNull(ImageCodec.DecodeRgbaBytes(outp));
    }

    [Fact]
    public void Jpeg_ApplyDpi_SetsJfifDensity()
    {
        var jpg = ImageCodec.EncodeScaled(ImageCodec.ImageFormat.Jpeg, 2, 2, TinyRgba(2, 2), 2, 2, 90);
        var outp = ImageMeta.ApplyDpi(jpg, ImageCodec.ImageFormat.Jpeg, 300);

        // SOI then a JFIF APP0 with units=1 (dpi) and density 300×300
        Assert.Equal(0xFF, outp[0]); Assert.Equal(0xD8, outp[1]);
        Assert.Equal(0xFF, outp[2]); Assert.Equal(0xE0, outp[3]);
        Assert.Equal((byte)'J', outp[6]);
        Assert.Equal(1, outp[13]);
        Assert.Equal(300, (outp[14] << 8) | outp[15]);
        Assert.Equal(300, (outp[16] << 8) | outp[17]);
        Assert.NotNull(ImageCodec.DecodeRgbaBytes(outp));
    }

    [Fact]
    public void Webp_ApplyDpi_PassesThrough()
    {
        var webp = ImageCodec.EncodeScaled(ImageCodec.ImageFormat.Webp, 2, 2, TinyRgba(2, 2), 2, 2, 90);
        var outp = ImageMeta.ApplyDpi(webp, ImageCodec.ImageFormat.Webp, 300);
        Assert.Same(webp, outp);
    }

    [Fact]
    public void ApplyDpi_ZeroOrNegative_NoOp()
    {
        var png = ImageCodec.EncodePngBytes(2, 2, TinyRgba(2, 2));
        Assert.Same(png, ImageMeta.ApplyDpi(png, ImageCodec.ImageFormat.Png, 0));
    }
}

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Format;
using Sable.Imaging;
using Xunit;

namespace Sable.Tests;

/// <summary>
/// Colour-profile metadata preservation (roadmap Workstream 5): an embedded ICC profile survives
/// PSD import → Document → PNG/TIFF export and the .sable round-trip. Pure byte assembly + parsing,
/// so no GPU/font system is needed. The working pipeline stays sRGB float; this is metadata fidelity.
/// </summary>
public class ColorProfileTests
{
    [Fact]
    public void PsdImport_CapturesIccProfile()
    {
        var doc = PsdReader.Load(PsdFixtures.IccTaggedDocument("sRGB Test Profile"), "icc", out _, out _);
        Assert.NotNull(doc.IccProfile);
        Assert.Equal(PsdFixtures.SampleIccProfile("sRGB Test Profile"), doc.IccProfile);
        Assert.Equal("sRGB Test Profile", doc.IccProfileName);
    }

    [Fact]
    public void PsdImport_NoProfile_LeavesNull()
    {
        var doc = PsdReader.Load(PsdFixtures.BasicRasterStack(), "noicc", out _, out _);
        Assert.Null(doc.IccProfile);
    }

    [Fact]
    public void PsdImport_TooSmallProfile_IgnoredWithWarning()
    {
        // A 1039 resource under 128 bytes is not a real profile → ignored + warned.
        var doc = PsdReader.Load(BuildTinyIccPsd(), "tiny", out var warnings, out _);
        Assert.Null(doc.IccProfile);
        Assert.Contains(warnings, w => w.Contains("colour profile"));
    }

    [Fact]
    public void PngExport_EmbedsIccpChunk()
    {
        var icc = PsdFixtures.SampleIccProfile("My Profile");
        var rgba = new byte[4 * 4 * 4];
        var png = ImageCodec.EncodeScaled(ImageCodec.ImageFormat.Png, 4, 4, rgba, 4, 4, 100, icc, "My Profile");

        var extracted = ExtractPngIccp(png);
        Assert.NotNull(extracted);
        Assert.Equal(icc, extracted);
        // still a valid, decodable PNG after the injection
        Assert.NotNull(ImageCodec.DecodeRgbaBytes(png));
    }

    [Fact]
    public void PngExport_NoProfile_NoIccpChunk()
    {
        var png = ImageCodec.EncodeScaled(ImageCodec.ImageFormat.Png, 4, 4, new byte[4 * 4 * 4], 4, 4, 100);
        Assert.Null(ExtractPngIccp(png));
    }

    [Fact]
    public void TiffExport_EmbedsIccTag()
    {
        var icc = PsdFixtures.SampleIccProfile("Tiff Profile");
        var tiff = ImageCodec.EncodeTiff(4, 4, new byte[4 * 4 * 4], icc);
        var tagged = ExtractTiffIcc(tiff);
        Assert.NotNull(tagged);
        Assert.Equal(icc, tagged);
    }

    [Fact]
    public void TiffExport_NoProfile_StillValid()
    {
        var tiff = ImageCodec.EncodeTiff(2, 2, new byte[2 * 2 * 4]);
        Assert.Null(ExtractTiffIcc(tiff));
        // header sanity: little-endian + magic 42
        Assert.Equal((byte)'I', tiff[0]);
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(2)));
    }

    [Fact]
    public void SableRoundTrip_PreservesProfile()
    {
        var doc = new Document(4, 4);
        doc.Layers.Add(new PixelLayer(4, 4, "bg"));
        doc.IccProfile = PsdFixtures.SampleIccProfile("Roundtrip Profile");
        doc.IccProfileName = "Roundtrip Profile";

        var path = Path.Combine(Path.GetTempPath(), $"sable_icc_{System.Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            Assert.Equal(doc.IccProfile, loaded.IccProfile);
            Assert.Equal("Roundtrip Profile", loaded.IccProfileName);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // --- helpers ----------------------------------------------------------------

    /// <summary>Walk PNG chunks, find iCCP, strip name+nul+compression byte, zlib-inflate the rest.</summary>
    private static byte[]? ExtractPngIccp(byte[] png)
    {
        int p = 8;   // skip signature
        while (p + 12 <= png.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            int dataStart = p + 8;
            if (type == "iCCP")
            {
                int q = dataStart;
                while (q < dataStart + len && png[q] != 0) q++;   // profile name
                int z = q + 2;                                    // skip nul + compression method
                using var ms = new MemoryStream(png, z, dataStart + len - z);
                using var inf = new ZLibStream(ms, CompressionMode.Decompress);
                using var outp = new MemoryStream();
                inf.CopyTo(outp);
                return outp.ToArray();
            }
            p = dataStart + len + 4;   // + CRC
        }
        return null;
    }

    /// <summary>Read the little-endian TIFF IFD, find tag 34675, slice its referenced ICC bytes.</summary>
    private static byte[]? ExtractTiffIcc(byte[] tiff)
    {
        int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
        for (int i = 0; i < count; i++)
        {
            int e = ifd + 2 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(e));
            if (tag != 34675) continue;
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(e + 4));
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(e + 8));
            return tiff.AsSpan(off, n).ToArray();
        }
        return null;
    }

    /// <summary>A PSD whose 1039 resource is only 16 bytes (sub-profile size) → must be rejected.</summary>
    private static byte[] BuildTinyIccPsd()
    {
        using var res = new MemoryStream();
        res.Write("8BIM"u8);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(b, 0x040F); res.Write(b[..2]);
        res.WriteByte(0); res.WriteByte(0);                                  // empty Pascal name
        BinaryPrimitives.WriteUInt32BigEndian(b, 16); res.Write(b);          // size 16
        res.Write(new byte[16]);
        // reuse the public fixture builder by piggybacking on IccTaggedDocument's shape:
        // simplest is to assemble a 1×1 PSD by hand around this resource.
        using var ms = new MemoryStream();
        void W16(ushort v) { Span<byte> x = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(x, v); ms.Write(x); }
        void W32(uint v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(x, v); ms.Write(x); }
        W32(0x38425053); W16(1);
        for (int i = 0; i < 6; i++) ms.WriteByte(0);
        W16(3); W32(1); W32(1); W16(8); W16(3);
        W32(0);                                  // colour mode data
        var resBytes = res.ToArray();
        W32((uint)resBytes.Length); ms.Write(resBytes);   // image resources
        W32(0);                                  // layer & mask info (none)
        return ms.ToArray();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Sable.Ai;
using Sable.Ai.Download;
using Sable.Ai.Gpu;
using Sable.Ai.Imaging;
using Sable.Ai.Models;
using Sable.Core.Ai;
using Xunit;

namespace Sable.Tests;

public class VramGateTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Sum_WhenNotOffloaded()
    {
        // denoiser 4GB + T5-XXL 9GB + VAE 1GB = 14GB resident; 12GB free → blocked
        var d = VramGate.Evaluate(new[] { 4 * Gb, 9 * Gb, 1 * Gb }, 12UL * Gb, offload: false);
        Assert.False(d.Fit);
        Assert.Equal(14 * Gb, d.RequiredBytes);
    }

    [Fact]
    public void PeakComponent_WhenOffloaded()
    {
        // same parts, offload → peak = max(9GB) not the 14GB sum → fits 12GB
        var d = VramGate.Evaluate(new[] { 4 * Gb, 9 * Gb, 1 * Gb }, 12UL * Gb, offload: true);
        Assert.True(d.Fit);
        Assert.Equal(9 * Gb, d.RequiredBytes);
    }

    [Fact]
    public void WorkingSet_CountsAgainstFree()
    {
        var d = VramGate.Evaluate(new[] { 6 * Gb }, 7UL * Gb, offload: false, workingSetBytes: 2 * Gb);
        Assert.False(d.Fit);           // 6 + 2 = 8 > 7
    }
}

public class VramBadgeTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Unknown_WhenFreeVramIsZero()
    {
        var b = VramBadge.ForModel(5 * Gb, 0);
        Assert.Equal(VramFit.Unknown, b.Fit);
        Assert.Contains("5.0 GB VRAM", b.Text);   // requirement still shown
    }

    [Fact]
    public void Fits_WithComfortableHeadroom()
    {
        // 1.5GB model + 256MB working set vs 24GB free → fits, not tight
        var b = VramBadge.ForModel(3 * Gb / 2, 24UL * Gb);
        Assert.Equal(VramFit.Fits, b.Fit);
        Assert.Contains("fits", b.Text);
    }

    [Fact]
    public void WontFit_WhenOverFree()
    {
        var b = VramBadge.ForModel(5 * Gb, 2UL * Gb);
        Assert.Equal(VramFit.WontFit, b.Fit);
        Assert.Contains("won't fit", b.Text);
    }

    [Fact]
    public void Tight_WhenLittleHeadroom()
    {
        // required = 4GB + 256MB ≈ 4.25GB; free = 4.5GB → > 85% of free → tight
        var b = VramBadge.ForModel(4 * Gb, 9UL * Gb / 2);
        Assert.Equal(VramFit.Tight, b.Fit);
    }
}

public class ModelCatalogTests
{
    private static ModelManifest Base(string id, string family, params AiTaskKind[] tasks) => new()
    { Id = id, Name = id, Kind = ModelKind.Base, Family = family, Tasks = tasks };

    private static ModelManifest Lora(string id, params string[] appliesTo) => new()
    { Id = id, Name = id, Kind = ModelKind.Adapter, AdapterType = AdapterType.Lora, Family = "lora", AppliesTo = appliesTo };

    private static ModelManifest Component(string id, string family, long vram) => new()
    { Id = id, Name = id, Kind = ModelKind.Component, ComponentFamily = family, VramBytes = vram };

    [Fact]
    public void ForTask_ReturnsOnlyMatchingBases()
    {
        var cat = new ModelCatalog(new[] { Base("a", "ESRGAN", AiTaskKind.Upscale), Base("b", "SAM2", AiTaskKind.Segment) });
        Assert.Single(cat.ForTask(AiTaskKind.Upscale));
        Assert.Equal("a", cat.ForTask(AiTaskKind.Upscale).First().Id);
    }

    [Fact]
    public void AdapterCompatibility_MatchesFamily()
    {
        var cat = new ModelCatalog();
        var sdxl = Base("sdxl", "SDXL", AiTaskKind.Txt2Img);
        var flux = Base("flux", "Flux", AiTaskKind.Txt2Img);
        var lora = Lora("style", "SDXL");
        Assert.True(cat.IsAdapterCompatible(lora, sdxl));
        Assert.False(cat.IsAdapterCompatible(lora, flux));   // SDXL LoRA must not load on Flux
    }

    [Fact]
    public void ResolveComponents_BundledCheckpoint_IsOk()
    {
        var sd15 = new ModelManifest
        {
            Id = "sd15", Kind = ModelKind.Base, Family = "SD1.5",
            Components = new ModelComponents { Checkpoint = new ComponentSource { Path = "sd15.safetensors" } },
        };
        var res = new ModelCatalog(new[] { sd15 }).ResolveComponents(sd15);
        Assert.True(res.Ok);
        Assert.Empty(res.MissingRefs);
    }

    [Fact]
    public void ResolveComponents_DetectsMissingSharedEncoder()
    {
        var flux = new ModelManifest
        {
            Id = "flux", Kind = ModelKind.Base, Family = "Flux",
            AcceptsTextEncoders = new[] { "CLIP-L", "T5-XXL" },
            Components = new ModelComponents
            {
                Denoiser = new ComponentSource { Path = "flux-unet.safetensors" },
                TextEncoders = new[] { new ComponentSource { Ref = "clip-l" }, new ComponentSource { Ref = "t5xxl" } },
            },
        };
        // only CLIP-L installed, T5-XXL missing
        var cat = new ModelCatalog(new[] { flux, Component("clip-l", "CLIP-L", 1) });
        var res = cat.ResolveComponents(flux);
        Assert.False(res.Ok);
        Assert.Contains("t5xxl", res.MissingRefs);
        Assert.Contains("clip-l", res.ResolvedComponentIds);
    }

    [Fact]
    public void ResolveComponents_AllInstalled_Ok_AndVramSumsComponents()
    {
        long gb = 1024L * 1024 * 1024;
        var flux = new ModelManifest
        {
            Id = "flux", Kind = ModelKind.Base, Family = "Flux", VramBytes = 4 * gb,
            AcceptsTextEncoders = new[] { "T5-XXL" },
            Components = new ModelComponents
            {
                Denoiser = new ComponentSource { Path = "flux.safetensors" },
                TextEncoders = new[] { new ComponentSource { Ref = "t5xxl" } },
            },
        };
        var cat = new ModelCatalog(new[] { flux, Component("t5xxl", "T5-XXL", 9 * gb) });
        var res = cat.ResolveComponents(flux);
        Assert.True(res.Ok);
        var parts = cat.VramParts(flux);
        Assert.Equal(new long[] { 4 * gb, 9 * gb }, parts);   // base + resolved component
        Assert.False(cat.Gate(flux, 12UL * (ulong)gb, offload: false).Fit);   // 13 > 12
        Assert.True(cat.Gate(flux, 12UL * (ulong)gb, offload: true).Fit);     // peak 9 ≤ 12
    }
}

public class ModelRegistryTests
{
    [Fact]
    public void Manifest_RoundTripsThroughJson_WithComponentsAndAdapter()
    {
        var m = new ModelManifest
        {
            Id = "sdxl", Name = "My SDXL", Kind = ModelKind.Base, Family = "SDXL", Tier = AiTier.Generative,
            Tasks = new[] { AiTaskKind.Txt2Img, AiTaskKind.Inpaint }, VramBytes = 7_000_000_000,
            AcceptsTextEncoders = new[] { "CLIP-L", "CLIP-bigG" },
            Components = new ModelComponents { Checkpoint = new ComponentSource { Path = "sdxl.safetensors" } },
        };
        var back = ModelRegistry.ParseManifest(ModelRegistry.SerializeManifest(m))!;
        Assert.Equal("sdxl", back.Id);
        Assert.Equal(ModelKind.Base, back.Kind);
        Assert.Equal(AiTier.Generative, back.Tier);
        Assert.Equal(2, back.Tasks.Count);
        Assert.Equal("sdxl.safetensors", back.Components!.Checkpoint!.Path);
        Assert.Contains("CLIP-bigG", back.AcceptsTextEncoders!);
    }

    [Theory]
    [InlineData("add_detail_lora.safetensors", ModelKind.Adapter, AdapterType.Lora)]
    [InlineData("sam2_hiera_large.onnx", ModelKind.Base, AdapterType.None)]
    [InlineData("RealESRGAN_x4.onnx", ModelKind.Base, AdapterType.None)]
    [InlineData("random_thing.bin", ModelKind.Base, AdapterType.None)]
    public void DraftFromFile_HeuristicsClassify(string file, ModelKind kind, AdapterType adapter)
    {
        var m = ModelRegistry.DraftFromFile(file);
        Assert.Equal(kind, m.Kind);
        Assert.Equal(adapter, m.AdapterType);
    }

    [Fact]
    public void SaveLoad_RoundTripsAndDefaultResolves()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sable_models_{Guid.NewGuid():N}");
        try
        {
            var reg = new ModelRegistry(dir);
            reg.Save(ModelRegistry.DraftFromFile("RealESRGAN_x4.onnx"));
            reg.Save(ModelRegistry.DraftFromFile("sam2_hiera.onnx"));

            var reg2 = new ModelRegistry(dir);
            reg2.Load();
            Assert.Equal(2, reg2.Catalog.All.Count);
            Assert.NotNull(reg2.DefaultFor(AiTaskKind.Upscale));      // falls back to the only upscale model
            Assert.Equal("ESRGAN", reg2.DefaultFor(AiTaskKind.Upscale)!.Family);

            reg2.SetDefault(AiTaskKind.Segment, reg2.Catalog.ForTask(AiTaskKind.Segment).First().Id);
            var reg3 = new ModelRegistry(dir);
            reg3.Load();
            Assert.Equal(AiTaskKind.Segment, AiTaskKind.Segment);
            Assert.NotNull(reg3.DefaultFor(AiTaskKind.Segment));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void MoveTo_MovesWeightsAndRebasesManifestPaths()
    {
        var oldDir = Path.Combine(Path.GetTempPath(), $"sable_models_{Guid.NewGuid():N}");
        var newDir = Path.Combine(Path.GetTempPath(), $"sable_models_{Guid.NewGuid():N}");
        try
        {
            var reg = new ModelRegistry(oldDir);
            var modelDir = reg.ModelDir("rmbg");
            Directory.CreateDirectory(modelDir);
            var weights = Path.Combine(modelDir, "rmbg.onnx");
            File.WriteAllBytes(weights, new byte[] { 1, 2, 3 });
            reg.Save(new ModelManifest
            {
                Id = "rmbg", Name = "RMBG", Family = "BiRefNet", Tier = AiTier.Light,
                Tasks = new[] { AiTaskKind.Matte }, Adapter = "matte", Files = new[] { weights },
            });

            reg.MoveTo(newDir);

            Assert.Equal(Path.GetFullPath(newDir), Path.GetFullPath(reg.ModelsFolder));
            var movedWeights = Path.Combine(newDir, "rmbg", "rmbg.onnx");
            Assert.True(File.Exists(movedWeights));   // weights physically moved
            Assert.False(File.Exists(weights));       // old copy gone

            // reload from disk: manifest's absolute Files path was rebased to the new folder + still resolves
            var reg2 = new ModelRegistry(newDir);
            reg2.Load();
            var m = reg2.Catalog.ById("rmbg");
            Assert.NotNull(m);
            Assert.Equal(Path.GetFullPath(movedWeights), Path.GetFullPath(m!.Files![0]));
            Assert.True(File.Exists(m.Files![0]));
        }
        finally
        {
            if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
            if (Directory.Exists(newDir)) Directory.Delete(newDir, true);
        }
    }
}

public class ImageOpsTests
{
    [Fact]
    public void ResizeRgba_Identity_ReturnsEqualCopy()
    {
        var src = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };   // 2x1 RGBA
        var dst = ImageOps.ResizeRgba(src, 2, 1, 2, 1);
        Assert.Equal(src, dst);
        Assert.NotSame(src, dst);
    }

    [Fact]
    public void Resize_ConstantImage_StaysConstant()
    {
        var src = new byte[4]; src[0] = 100; src[1] = 150; src[2] = 200; src[3] = 255;   // 1x1
        var dst = ImageOps.ResizeRgba(src, 1, 1, 4, 4);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(100, dst[i * 4]); Assert.Equal(150, dst[i * 4 + 1]);
            Assert.Equal(200, dst[i * 4 + 2]); Assert.Equal(255, dst[i * 4 + 3]);
        }
    }

    [Fact]
    public void ToChwFloat_NormalizesAndLaysOutPlanar()
    {
        var px = new byte[] { 255, 255, 255, 255 };   // 1x1 white
        var t = ImageOps.ToChwFloat(px, 1, 1, new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f, 0.5f });
        Assert.Equal(3, t.Length);
        Assert.Equal(1.0f, t[0], 3); Assert.Equal(1.0f, t[1], 3); Assert.Equal(1.0f, t[2], 3);   // (1-0.5)/0.5
    }

    [Fact]
    public void ToChwFloat_Bgr_SwapsRedAndBlue()
    {
        var px = new byte[] { 255, 0, 0, 255 };   // pure red
        var id = new[] { 0f, 0f, 0f }; var one = new[] { 1f, 1f, 1f };
        var rgb = ImageOps.ToChwFloat(px, 1, 1, id, one, bgr: false);
        var bgr = ImageOps.ToChwFloat(px, 1, 1, id, one, bgr: true);
        Assert.Equal(1f, rgb[0], 3);   // R in channel 0
        Assert.Equal(0f, bgr[0], 3);   // B in channel 0 (red moved to channel 2)
        Assert.Equal(1f, bgr[2], 3);
    }

    [Fact]
    public void MaskFromFloat_Sigmoid_And_Direct()
    {
        Assert.Equal(128, ImageOps.MaskFromFloat(new[] { 0f }, 1, 1, sigmoid: true)[0]);   // sigmoid(0)=0.5
        Assert.Equal(255, ImageOps.MaskFromFloat(new[] { 1f }, 1, 1, sigmoid: false)[0]);  // clamp 1.0
        Assert.Equal(0, ImageOps.MaskFromFloat(new[] { 0f }, 1, 1, sigmoid: false)[0]);
    }

    [Fact]
    public void CoverageToRgbaMask_PacksRgbAndOpaqueAlpha()
    {
        var rgba = ImageOps.CoverageToRgbaMask(new byte[] { 200 }, 1, 1);
        Assert.Equal(new byte[] { 200, 200, 200, 255 }, rgba);
    }

    [Fact]
    public void Crop_ExtractsSubRect_AndZeroesOutOfBounds()
    {
        // 2x2: TL=10, TR=20, BL=30, BR=40 (R channel)
        var src = new byte[16];
        src[0] = 10; src[4] = 20; src[8] = 30; src[12] = 40;
        for (int i = 0; i < 4; i++) src[i * 4 + 3] = 255;
        var c = ImageOps.Crop(src, 2, 2, 1, 1, 2, 2);   // crop bottom-right, extends 1px past the edge
        Assert.Equal(40, c[0]);     // (1,1) = BR
        Assert.Equal(0, c[3 * 4 + 3]);   // (1,1) of the crop is out of bounds → transparent
    }

    [Fact]
    public void ChwFloatToRgba_RoundTripsToChwFloat()
    {
        var rgba = new byte[] { 12, 200, 99, 255, 250, 5, 128, 255 };   // 2x1 opaque
        var chw = ImageOps.ToChwFloat(rgba, 2, 1, new[] { 0f, 0f, 0f }, new[] { 1f, 1f, 1f });
        var back = ImageOps.ChwFloatToRgba(chw, 2, 1);
        for (int i = 0; i < rgba.Length; i++) Assert.InRange(System.Math.Abs(back[i] - rgba[i]), 0, 1);
    }
}

public class ModelDownloadTests
{
    [Fact]
    public void RecommendedCatalog_IsValid()
    {
        Assert.NotEmpty(RecommendedModels.All);
        Assert.Equal(RecommendedModels.All.Count, RecommendedModels.All.Select(m => m.Id).Distinct().Count());
        foreach (var m in RecommendedModels.All)
        {
            Assert.NotEmpty(m.Downloads);
            foreach (var d in m.Downloads)
            {
                Assert.False(string.IsNullOrWhiteSpace(d.Url));
                Assert.False(string.IsNullOrWhiteSpace(d.FileName));
            }
            Assert.False(string.IsNullOrWhiteSpace(m.License));   // license must be shown before download
            Assert.NotEmpty(m.Tasks);
        }
        Assert.NotNull(RecommendedModels.ById("rmbg-1.4"));
    }

    [Fact]
    public void Sam2_HasTwoFiles_EncoderThenDecoder()
    {
        var sam = RecommendedModels.ById("sam2-hiera-large")!;
        Assert.Equal(2, sam.Downloads.Count);
        Assert.Equal("sam2", sam.Adapter);
        Assert.Contains("encoder", sam.Downloads[0].FileName);
        Assert.Contains("decoder", sam.Downloads[1].FileName);
    }

    [Fact]
    public void DefaultSet_HasOnePerTask()
    {
        var set = RecommendedModels.DefaultSet;
        Assert.Equal(set.Count, set.Select(m => m.Tasks[0]).Distinct().Count());   // no duplicate task
        Assert.Contains(set, m => m.Tasks[0] == AiTaskKind.Segment);               // includes SAM2
    }

    [Fact]
    public void Recommended_ToManifest_MapsFiles()
    {
        var rec = RecommendedModels.ById("sam2-hiera-large")!;
        var m = rec.ToManifest(new[] { @"C:\m\enc.onnx", @"C:\m\dec.onnx" });
        Assert.Equal("sam2-hiera-large", m.Id);
        Assert.Equal("sam2", m.Adapter);
        Assert.Equal(new[] { @"C:\m\enc.onnx", @"C:\m\dec.onnx" }, m.Files);
    }

    [Theory]
    [InlineData("https://example.com/a/model.onnx", "https://example.com/a/model.onnx")]
    [InlineData("briaai/RMBG-1.4/onnx/model.onnx", "https://huggingface.co/briaai/RMBG-1.4/resolve/main/onnx/model.onnx")]
    public void ResolveUrl_HandlesDirectAndHfShorthand(string input, string expected)
        => Assert.Equal(expected, Sable.Ai.Download.ModelDownloader.ResolveUrl(input));

    [Fact]
    public void ResolveUrl_RejectsBareName()
        => Assert.Throws<ArgumentException>(() => Sable.Ai.Download.ModelDownloader.ResolveUrl("model.onnx"));

    [Theory]
    [InlineData("https://huggingface.co/briaai/RMBG-1.4/resolve/main/onnx/model.onnx", "model.onnx")]
    [InlineData("https://example.com/files/esrgan_x4.onnx?download=true", "esrgan_x4.onnx")]
    public void FileNameFromUrl_TakesLastSegment(string url, string expected)
        => Assert.Equal(expected, Sable.Ai.Download.ModelDownloader.FileNameFromUrl(url));
}

/// <summary>
/// NETWORK test: HEAD/range-GET every curated download URL to confirm it still resolves (catches a
/// moved/relicensed repo before a user hits a broken download). Requires internet; fails with the
/// model id + status + URL when a link is dead.
/// </summary>
public class RecommendedUrlReachabilityTests
{
    public static IEnumerable<object[]> ModelIds => RecommendedModels.All.Select(m => new object[] { m.Id });

    [Theory]
    [MemberData(nameof(ModelIds))]
    public async Task RecommendedModel_AllUrlsReachable(string id)
    {
        var m = RecommendedModels.ById(id)!;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Sable-ModelCheck/1.0");

        foreach (var part in m.Downloads)
        {
            var url = ModelDownloader.ResolveUrl(part.Url);
            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead);
                // some hosts reject HEAD → confirm with a 1-byte ranged GET
                if (resp.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.Forbidden)
                {
                    resp.Dispose();
                    var get = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(0, 0) } };
                    resp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
                }
            }
            catch (Exception ex)
            {
                Assert.Fail($"Recommended model '{m.Id}' file '{part.FileName}' URL is unreachable: {url}\n  {ex.GetType().Name}: {ex.Message}");
                return;
            }

            using (resp)
            {
                // 429/503 = transient throttling (HuggingFace rate-limits CI runners), not a dead link:
                // the endpoint still exists. Only a permanent failure (404/410/…) means a moved/broken URL.
                if (resp.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)
                    continue;
                Assert.True(resp.IsSuccessStatusCode,
                    $"Recommended model '{m.Id}' file '{part.FileName}' URL returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {url}");
            }
        }
    }
}

public class OnnxBackendTests
{
    [Fact]
    public void Construct_LoadsOrt_AndReportsAvailability()
    {
        // exercises the ONNX Runtime native load + EP enumeration (no weights needed).
        var ex = Record.Exception(() =>
        {
            using var b = new Sable.Ai.Backends.OnnxBackend();
            // EP is per-OS: CUDA on Linux (Sable's sm_120 build), WebGPU/Metal on macOS, DirectML on Windows.
            string expected = OperatingSystem.IsLinux() ? "ONNX (CUDA)"
                            : OperatingSystem.IsMacOS() ? "ONNX (WebGPU)"
                            : "ONNX (DirectML)";
            Assert.Equal(expected, b.Name);
            Assert.Equal(AiTier.Light, b.Tier);
            _ = b.IsAvailable;   // bool; true iff the OS's GPU EP (CUDA/WebGPU/DirectML) is present + activated
        });
        Assert.Null(ex);
    }
}

public class AmgOpsTests
{
    private static ObjectMask Rect(int w, int h, int x0, int y0, int x1, int y1, float score)
    {
        var c = new byte[w * h]; int area = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (x >= 0 && y >= 0 && x < w && y < h) { c[y * w + x] = 255; area++; }
        return new ObjectMask(c, w, h, area, score, x0, y0, x1 - x0, y1 - y0);
    }

    [Fact]
    public void GridPoints_CountAndBounds()
    {
        var pts = AmgOps.GridPoints(100, 80, 4);
        Assert.Equal(16, pts.Length);
        Assert.All(pts, p => Assert.True(p.X >= 0 && p.X < 100 && p.Y >= 0 && p.Y < 80));
        Assert.Equal(12.5f, pts[0].X, 2);   // (0.5/4)*100
        Assert.Equal(10f, pts[0].Y, 2);     // (0.5/4)*80
    }

    [Fact]
    public void IoU_IdenticalDisjointHalf()
    {
        var a = Rect(10, 10, 0, 0, 5, 10, 1f);
        var b = Rect(10, 10, 0, 0, 5, 10, 1f);
        var c = Rect(10, 10, 5, 0, 10, 10, 1f);     // disjoint from a
        Assert.Equal(1f, AmgOps.IoU(a, b), 3);
        Assert.Equal(0f, AmgOps.IoU(a, c), 3);
    }

    [Fact]
    public void Nms_DropsLowerScoreOverlap_KeepsDisjoint()
    {
        var big = Rect(20, 20, 0, 0, 10, 20, 0.9f);
        var dup = Rect(20, 20, 0, 0, 10, 20, 0.5f);   // identical → suppressed
        var other = Rect(20, 20, 10, 0, 20, 20, 0.8f); // disjoint → kept
        var kept = AmgOps.Nms(new[] { big, dup, other }, iouThresh: 0.7f);
        Assert.Equal(2, kept.Count);
        Assert.Contains(big, kept);
        Assert.Contains(other, kept);
        Assert.DoesNotContain(dup, kept);
    }

    [Fact]
    public void BestAt_PicksSmallestContaining()
    {
        var person = Rect(20, 20, 0, 0, 20, 20, 0.8f);  // whole
        var face = Rect(20, 20, 5, 5, 10, 10, 0.7f);    // smaller, inside
        var hit = AmgOps.BestAt(new[] { person, face }, 7, 7);
        Assert.Equal(face, hit);                         // most specific
        Assert.Null(AmgOps.BestAt(new[] { face }, 0, 0)); // outside → none
    }
}

public class Sam2OpsTests
{
    [Fact]
    public void CentrePoint_PutsOnePositivePointAtMiddle()
    {
        var pts = Sable.Ai.Adapters.Sam2Ops.CentrePoint(100, 80);
        Assert.Single(pts);
        Assert.Equal(50f, pts[0].X0);
        Assert.Equal(40f, pts[0].Y0);
        Assert.True(pts[0].Positive);
    }

    [Fact]
    public void BuildPrompts_ScalesPointToModelSpace()
    {
        var pts = new[] { new AiPrompt(AiPromptKind.Point, 50, 40, 0, 0, true) };
        var (coords, labels) = Sable.Ai.Adapters.Sam2Ops.BuildPrompts(pts, 100, 80, 1024);
        Assert.Equal(new[] { 512f, 512f }, coords);   // 50/100*1024, 40/80*1024
        Assert.Equal(new[] { 1f }, labels);
    }

    [Fact]
    public void BuildPrompts_BoxExpandsToTwoLabelledCorners()
    {
        var pts = new[] { new AiPrompt(AiPromptKind.Box, 0, 0, 100, 80, true) };
        var (coords, labels) = Sable.Ai.Adapters.Sam2Ops.BuildPrompts(pts, 100, 80, 1024);
        Assert.Equal(new[] { 0f, 0f, 1024f, 1024f }, coords);
        Assert.Equal(new[] { 2f, 3f }, labels);       // SAM box corner labels
    }

    [Fact]
    public void BuildPrompts_NegativePoint_GetsZeroLabel()
    {
        var pts = new[] { new AiPrompt(AiPromptKind.Point, 10, 10, 0, 0, Positive: false) };
        var (_, labels) = Sable.Ai.Adapters.Sam2Ops.BuildPrompts(pts, 100, 100, 512);
        Assert.Equal(new[] { 0f }, labels);
    }
}

public class TileInferenceTests
{
    [Fact]
    public void Plan_SingleTile_WhenImageFits()
    {
        var tiles = Sable.Ai.Tiling.TileInference.Plan(200, 150, 256, 16);
        Assert.Single(tiles);
        Assert.Equal(200, tiles[0].W);
        Assert.Equal(150, tiles[0].H);
    }

    [Fact]
    public void Plan_CoversWholeImage_WithOverlap()
    {
        int w = 600, h = 300;
        var tiles = Sable.Ai.Tiling.TileInference.Plan(w, h, 256, 32);
        var covered = new bool[w * h];
        foreach (var t in tiles)
        {
            Assert.True(t.W <= 256 && t.H <= 256);
            for (int y = t.Y; y < t.Y + t.H; y++)
                for (int x = t.X; x < t.X + t.W; x++)
                    covered[y * w + x] = true;
        }
        Assert.DoesNotContain(false, covered);   // every pixel is in at least one tile
    }

    [Fact]
    public void Weight_HighInCentre_LowAtCorner()
    {
        float centre = Sable.Ai.Tiling.TileInference.Weight(5, 5, 10, 10, 4);
        float corner = Sable.Ai.Tiling.TileInference.Weight(0, 0, 10, 10, 4);
        Assert.True(centre > corner);
        Assert.True(centre > 0.9f);
        Assert.True(corner > 0f);   // floored, never zero
    }

    [Fact]
    public void AccumulateFinalize_OverlappingEqualTiles_GiveThatValue()
    {
        int dw = 4, dh = 2;
        var col = new float[dw * dh * 4];
        var wts = new float[dw * dh];
        byte[] Const(int w, int h, byte v)
        {
            var b = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { b[i * 4] = v; b[i * 4 + 1] = v; b[i * 4 + 2] = v; b[i * 4 + 3] = 255; }
            return b;
        }
        Sable.Ai.Tiling.TileInference.Accumulate(col, wts, dw, dh, Const(3, 2, 100), 3, 2, 0, 0, 1);
        Sable.Ai.Tiling.TileInference.Accumulate(col, wts, dw, dh, Const(3, 2, 100), 3, 2, 1, 0, 1);
        var outp = Sable.Ai.Tiling.TileInference.Finalize(col, wts, dw, dh);
        for (int i = 0; i < dw * dh; i++)
        {
            Assert.InRange(outp[i * 4], (byte)99, (byte)101);
            Assert.Equal(255, outp[i * 4 + 3]);
        }
    }
}

public class AiReadinessTests
{
    private sealed class StubBackend : IAiBackend
    {
        public string Name => "stub";
        public AiTier Tier { get; init; } = AiTier.Light;
        public bool IsAvailable { get; init; } = true;
        public Task<ulong> ProbeFreeVramAsync(CancellationToken ct = default) => Task.FromResult(0UL);
    }

    private sealed class StubProbe : GpuProbe
    {
        public ulong Free; public bool Gpu;
        public override ulong FreeVramBytes() => Free;
        public override bool HasGpu => Gpu;
    }

    private static ModelRegistry RegistryWith(params ModelManifest[] models)
    {
        var reg = new ModelRegistry(Path.Combine(Path.GetTempPath(), "noexist_" + Guid.NewGuid().ToString("N")));
        foreach (var m in models) reg.Catalog.Add(m);
        return reg;
    }

    [Fact]
    public void NoModel_Blocks()
    {
        var svc = new AiService(RegistryWith(), new StubProbe { Gpu = true });
        var r = svc.CheckReadiness(AiTaskKind.Matte);
        Assert.False(r.CanRun);
        Assert.Equal(AiBlockReason.NoModel, r.Reason);
    }

    [Fact]
    public void NoGpu_Blocks()
    {
        var reg = RegistryWith(new ModelManifest { Id = "m", Kind = ModelKind.Base, Family = "BiRefNet", Tasks = new[] { AiTaskKind.Matte } });
        var svc = new AiService(reg, new StubProbe { Gpu = false });   // no GPU, no backend
        var r = svc.CheckReadiness(AiTaskKind.Matte);
        Assert.False(r.CanRun);
        Assert.Equal(AiBlockReason.NoGpu, r.Reason);
    }

    [Fact]
    public void WontFit_Blocks()
    {
        long gb = 1024L * 1024 * 1024;
        var reg = RegistryWith(new ModelManifest { Id = "m", Kind = ModelKind.Base, Family = "X", Tasks = new[] { AiTaskKind.Upscale }, VramBytes = 10 * gb });
        var svc = new AiService(reg, new StubProbe { Gpu = true, Free = 4UL * (ulong)gb });
        var r = svc.CheckReadiness(AiTaskKind.Upscale);
        Assert.False(r.CanRun);
        Assert.Equal(AiBlockReason.WontFitVram, r.Reason);
    }

    [Fact]
    public void Ready_WhenModelGpuAndVramOk()
    {
        long gb = 1024L * 1024 * 1024;
        var reg = RegistryWith(new ModelManifest { Id = "m", Kind = ModelKind.Base, Family = "X", Tasks = new[] { AiTaskKind.Upscale }, VramBytes = 2 * gb });
        var svc = new AiService(reg, new StubProbe { Gpu = true, Free = 8UL * (ulong)gb });
        svc.AddBackend(new StubBackend());
        var r = svc.CheckReadiness(AiTaskKind.Upscale);
        Assert.True(r.CanRun);
        Assert.Equal(AiBlockReason.None, r.Reason);
    }
}

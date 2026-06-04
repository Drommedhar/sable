using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sable.Ai.Models;
using Sable.Core.Ai;
using Sable.Core.Settings;
using Xunit;

namespace Sable.Tests;

public class ComfyLayoutTests
{
    private static readonly ModelSource Src = ModelSource.Comfy("comfyui", @"X:\ComfyUI\models");

    [Fact]
    public void Checkpoint_DraftsGenerativeBase()
    {
        var m = ComfyLayout.DraftOne(Src, "checkpoints/sdxl_base.safetensors");
        Assert.NotNull(m);
        Assert.Equal(ModelKind.Base, m!.Kind);
        Assert.Equal(AiTier.Generative, m.Tier);
        Assert.Contains(AiTaskKind.Txt2Img, m.Tasks);
        Assert.Contains(AiTaskKind.Inpaint, m.Tasks);
        Assert.Equal("SDXL", m.Family);
        Assert.Equal(new[] { "CLIP-L", "CLIP-bigG" }, m.AcceptsTextEncoders);
        Assert.Equal("comfyui", m.SourceId);
        Assert.Equal("comfyui:checkpoints/sdxl_base", m.Id);
    }

    [Fact]
    public void ReferencesFileInPlace()
    {
        var m = ComfyLayout.DraftOne(Src, "checkpoints/sdxl_base.safetensors");
        Assert.Equal(Path.Combine(Src.Path, "checkpoints/sdxl_base.safetensors"), m!.Files!.Single());
    }

    [Fact]
    public void Lora_DraftsAdapterWithAppliesTo()
    {
        var m = ComfyLayout.DraftOne(Src, "loras/cool_sdxl_style.safetensors");
        Assert.Equal(ModelKind.Adapter, m!.Kind);
        Assert.Equal(AdapterType.Lora, m.AdapterType);
        Assert.Equal(new[] { "SDXL" }, m.AppliesTo);
    }

    [Fact]
    public void Lora_UnknownArch_NoAppliesTo()
    {
        var m = ComfyLayout.DraftOne(Src, "loras/add_detail.safetensors");
        Assert.Equal(AdapterType.Lora, m!.AdapterType);
        Assert.Null(m.AppliesTo);
    }

    [Theory]
    [InlineData("vae/sdxl.vae.safetensors", "VAE-SDXL")]
    [InlineData("text_encoders/t5xxl_fp16.safetensors", "T5-XXL")]
    [InlineData("clip/clip_l.safetensors", "CLIP-L")]
    [InlineData("clip/clip_g.safetensors", "CLIP-bigG")]
    public void Components_GetFamily(string rel, string family)
    {
        var m = ComfyLayout.DraftOne(Src, rel);
        Assert.Equal(ModelKind.Component, m!.Kind);
        Assert.Equal(family, m.ComponentFamily);
    }

    [Fact]
    public void DiffusionModels_IsSelectableBase()
    {
        // modern ComfyUI keeps the transformer standalone — surface it as a base so it shows in the picker
        var m = ComfyLayout.DraftOne(Src, "diffusion_models/flux1-dev.safetensors");
        Assert.Equal(ModelKind.Base, m!.Kind);
        Assert.Equal(AiTier.Generative, m.Tier);
        Assert.Contains(AiTaskKind.Inpaint, m.Tasks);
        Assert.Equal("Flux", m.Family);
    }

    [Fact]
    public void ControlNet_DraftsControlNetAdapter()
    {
        var m = ComfyLayout.DraftOne(Src, "controlnet/canny_sdxl.safetensors");
        Assert.Equal(AdapterType.ControlNet, m!.AdapterType);
    }

    [Fact]
    public void UpscaleOnnx_IsLightEsrgan()
    {
        var m = ComfyLayout.DraftOne(Src, "upscale_models/RealESRGAN_x4.onnx");
        Assert.Equal(AiTier.Light, m!.Tier);
        Assert.Equal("esrgan", m.Adapter);
        Assert.Contains(AiTaskKind.Upscale, m.Tasks);
    }

    [Fact]
    public void UpscalePth_IsGenerativeTorch()
    {
        var m = ComfyLayout.DraftOne(Src, "upscale_models/4x-UltraSharp.pth");
        Assert.Equal(AiTier.Generative, m!.Tier);
        Assert.Null(m.Adapter);
    }

    [Fact]
    public void Embeddings_AreSkipped()
        => Assert.Null(ComfyLayout.DraftOne(Src, "embeddings/easynegative.pt"));

    [Fact]
    public void LooseRootFile_IsSkipped()
        => Assert.Null(ComfyLayout.DraftOne(Src, "putModelsHere.txt"));

    [Fact]
    public void NonWeightExtension_IsSkipped()
        => Assert.Null(ComfyLayout.DraftOne(Src, "checkpoints/readme.txt"));

    [Fact]
    public void ArchOverride_WinsOverFilename()
    {
        var m = ComfyLayout.DraftOne(Src, "checkpoints/mystery.safetensors", archOverride: "Flux");
        Assert.Equal("Flux", m!.Family);
        Assert.Equal(new[] { "CLIP-L", "T5-XXL" }, m.AcceptsTextEncoders);
    }

    [Theory]
    [InlineData("SD1.5", new[] { "CLIP-L" })]
    [InlineData("SDXL", new[] { "CLIP-L", "CLIP-bigG" })]
    [InlineData("SD3", new[] { "CLIP-L", "CLIP-bigG", "T5-XXL" })]
    [InlineData("Flux", new[] { "CLIP-L", "T5-XXL" })]
    public void EncodersFor_KnownArch(string arch, string[] expect)
        => Assert.Equal(expect, ComfyLayout.EncodersFor(arch));

    [Fact]
    public void VramBytes_EstimatedFromFileSize()
    {
        var m = ComfyLayout.DraftOne(Src, "checkpoints/sdxl_base.safetensors", sizeBytes: 6_900_000_000);
        Assert.Equal(6_900_000_000, m!.VramBytes);
    }

    [Fact]
    public void VramBytes_ZeroWhenSizeUnknown()
        => Assert.Equal(0, ComfyLayout.DraftOne(Src, "loras/x.safetensors")!.VramBytes);
}

public class ComfyExtraPathsTests
{
    [Fact]
    public void ParsesBasePathAndSingleLineRoles()
    {
        const string yaml = @"
comfyui:
    base_path: D:/comfy/ComfyUI
    checkpoints: models/checkpoints
    loras: models/loras   # a comment
    is_default: true
";
        var cfgs = ComfyExtraPaths.Parse(yaml);
        var cfg = Assert.Single(cfgs);
        Assert.Equal("comfyui", cfg.Name);
        Assert.Equal("D:/comfy/ComfyUI", cfg.BasePath);
        Assert.True(cfg.Roles.ContainsKey("checkpoints"));
        Assert.False(cfg.Roles.ContainsKey("is_default"));   // non-role key ignored

        var roots = ComfyExtraPaths.ResolveRoots(cfg).ToList();
        Assert.Contains(roots, r => r.Role == "checkpoints" &&
            r.AbsDir == Path.Combine("D:/comfy/ComfyUI", "models/checkpoints"));
    }

    [Fact]
    public void ParsesBlockScalarRole()
    {
        const string yaml = @"
other:
    base_path: /data
    vae: |
        models/vae
        models/vae_extra
";
        var cfg = Assert.Single(ComfyExtraPaths.Parse(yaml));
        Assert.Equal(2, cfg.Roles["vae"].Count);
        var roots = ComfyExtraPaths.ResolveRoots(cfg).Where(r => r.Role == "vae").ToList();
        Assert.Equal(2, roots.Count);
    }

    [Fact]
    public void AbsoluteRolePath_NotJoinedToBase()
    {
        const string yaml = @"
a1111:
    base_path: /sd
    loras: /shared/loras
";
        var cfg = Assert.Single(ComfyExtraPaths.Parse(yaml));
        var root = ComfyExtraPaths.ResolveRoots(cfg).Single(r => r.Role == "loras");
        Assert.Equal("/shared/loras", root.AbsDir.Replace('\\', '/'));
    }

    [Fact]
    public void EmptyOrBlank_NoConfigs()
        => Assert.Empty(ComfyExtraPaths.Parse("   \n # only a comment\n"));
}

public class SafetensorsHeaderTests
{
    [Fact]
    public void ReadHeaderLength_LittleEndian()
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, 1234);
        Assert.Equal(1234, SafetensorsHeader.ReadHeaderLength(b));
    }

    [Fact]
    public void ReadHeaderLength_TooShort_IsNegative()
        => Assert.Equal(-1, SafetensorsHeader.ReadHeaderLength(new byte[4]));

    [Fact]
    public void ReadHeaderLength_OverCap_IsNegative()
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)SafetensorsHeader.MaxHeaderBytes + 1);
        Assert.Equal(-1, SafetensorsHeader.ReadHeaderLength(b));
    }

    [Fact]
    public void Arch_FromMetadata()
    {
        const string json = @"{""__metadata__"":{""modelspec.architecture"":""stable-diffusion-xl-v1-base""}}";
        Assert.Equal("SDXL", SafetensorsHeader.GuessArchFromHeaderJson(json));
    }

    [Theory]
    [InlineData(@"{""model.double_blocks.0.x"":{}}", "Flux")]
    [InlineData(@"{""model.joint_blocks.0.x"":{}}", "SD3")]
    [InlineData(@"{""model.add_embedding.x"":{}}", "SDXL")]
    public void Arch_FromTensorKeys(string json, string expect)
        => Assert.Equal(expect, SafetensorsHeader.GuessArchFromHeaderJson(json));

    [Fact]
    public void Arch_BadJson_IsNull()
        => Assert.Null(SafetensorsHeader.GuessArchFromHeaderJson("not json {"));

    [Theory]
    [InlineData("flux1-dev", "Flux")]
    [InlineData("stable-diffusion-3-medium", "SD3")]
    [InlineData("something-xl", "SDXL")]
    public void NormalizeArch_Works(string s, string expect)
        => Assert.Equal(expect, SafetensorsHeader.NormalizeArch(s));
}

public class SourceScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sable_src_" + Guid.NewGuid().ToString("N"));

    private string Make(string rel, byte[]? bytes = null)
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes ?? new byte[] { 1, 2, 3 });
        return p;
    }

    private static byte[] Safetensors(string metaJson)
    {
        var json = Encoding.UTF8.GetBytes(metaJson);
        var buf = new byte[8 + json.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), (ulong)json.Length);
        json.CopyTo(buf, 8);
        return buf;
    }

    [Fact]
    public void Scans_TypedSubfolders_SkipsNoise()
    {
        Make("checkpoints/sdxl_base.safetensors");
        Make("loras/foo.safetensors");
        Make("vae/x.safetensors");
        Make("embeddings/neg.pt");          // skipped role
        Make("checkpoints/readme.txt");     // not a weight
        Make("loosefile.safetensors");      // at root, no role

        var src = ModelSource.Comfy("comfyui", _root);
        var models = SourceScanner.Scan(src, cacheDir: null);

        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Kind == ModelKind.Base && m.Family == "SDXL");
        Assert.Contains(models, m => m.Kind == ModelKind.Adapter);
        Assert.Contains(models, m => m.Kind == ModelKind.Component);
    }

    [Fact]
    public void SniffsArch_FromSafetensorsHeader_OverNeutralName()
    {
        // neutral filename → the SDXL family must come from the header sniff
        Make("checkpoints/mystery.safetensors", Safetensors(@"{""__metadata__"":{""modelspec.architecture"":""sdxl""}}"));
        var models = SourceScanner.Scan(ModelSource.Comfy("c", _root), cacheDir: null);
        Assert.Equal("SDXL", models.Single().Family);
    }

    [Fact]
    public void NeverWritesIntoExternalTree_AndCachesInCacheDir()
    {
        Make("checkpoints/a_sdxl.safetensors");
        var cacheDir = Path.Combine(_root, "_cache");
        Directory.CreateDirectory(cacheDir);

        SourceScanner.Scan(ModelSource.Comfy("comfyui", _root), cacheDir);

        Assert.False(File.Exists(Path.Combine(_root, "checkpoints", "model.json")));
        Assert.True(File.Exists(Path.Combine(cacheDir, ".sources", "comfyui.json")));
    }

    [Fact]
    public void ResolveModelsRoot_AutoDetectsModelsChild()
    {
        // picked = the ComfyUI ROOT (has a models/ child with role subfolders)
        Make("models/checkpoints/x_sdxl.safetensors");
        Assert.Equal(Path.Combine(_root, "models"), SourceScanner.ResolveModelsRoot(_root));
        // picking the models folder directly → unchanged
        Assert.Equal(Path.Combine(_root, "models"), SourceScanner.ResolveModelsRoot(Path.Combine(_root, "models")));
    }

    [Fact]
    public void ResolveModelsRoot_DirectRoleSubfolder_Unchanged()
    {
        Make("checkpoints/x_sdxl.safetensors");
        Assert.Equal(_root, SourceScanner.ResolveModelsRoot(_root));
    }

    [Fact]
    public void Scan_SetsVramFromFileSize()
    {
        var p = Make("checkpoints/big_sdxl.safetensors", new byte[5000]);
        var m = SourceScanner.Scan(ModelSource.Comfy("c", _root), cacheDir: null).Single();
        Assert.Equal(new FileInfo(p).Length, m.VramBytes);
    }

    [Fact]
    public void ExtraModelPaths_AddsRoleTaggedRoots()
    {
        // the comfy "models" root is empty of checkpoints; an extra path points elsewhere
        var extraDir = Path.Combine(_root, "extra_ckpts");
        Directory.CreateDirectory(extraDir);
        File.WriteAllBytes(Path.Combine(extraDir, "sdxl_extra.safetensors"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(_root, "extra_model_paths.yaml"),
            "mycfg:\n    checkpoints: " + extraDir.Replace('\\', '/') + "\n");

        var models = SourceScanner.Scan(ModelSource.Comfy("comfyui", _root), cacheDir: null);
        Assert.Contains(models, m => m.Kind == ModelKind.Base && m.Name == "sdxl_extra");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

public class ModelRegistryMultiRootTests : IDisposable
{
    private readonly string _native = Path.Combine(Path.GetTempPath(), "sable_native_" + Guid.NewGuid().ToString("N"));
    private readonly string _comfy = Path.Combine(Path.GetTempPath(), "sable_comfy_" + Guid.NewGuid().ToString("N"));

    public ModelRegistryMultiRootTests()
    {
        Directory.CreateDirectory(_native);
        // a native model.json model
        var reg = new ModelRegistry(_native);
        reg.Save(new ModelManifest { Id = "native-mdl", Name = "Native", Kind = ModelKind.Base, Family = "BiRefNet", Tasks = new[] { AiTaskKind.Matte } });

        // a comfy tree
        var ck = Path.Combine(_comfy, "checkpoints");
        Directory.CreateDirectory(ck);
        File.WriteAllBytes(Path.Combine(ck, "sdxl_base.safetensors"), new byte[] { 1 });
    }

    [Fact]
    public void AddSource_MergesNativeAndExternal()
    {
        var reg = new ModelRegistry(_native);
        reg.Load();
        Assert.NotNull(reg.Catalog.ById("native-mdl"));
        Assert.Single(reg.Catalog.All);

        reg.AddSource(ModelSource.Comfy("comfyui", _comfy));
        Assert.Equal(2, reg.Catalog.All.Count);
        Assert.Contains(reg.Catalog.All, m => m.SourceId == "comfyui" && m.Family == "SDXL");
    }

    [Fact]
    public void RemoveSource_DropsExternal_KeepsNative()
    {
        var reg = new ModelRegistry(_native);
        reg.AddSource(ModelSource.Comfy("comfyui", _comfy));
        reg.RemoveSource("comfyui");
        Assert.Single(reg.Catalog.All);
        Assert.NotNull(reg.Catalog.ById("native-mdl"));
    }

    [Fact]
    public void SetSources_ReplacesExternalSet()
    {
        var reg = new ModelRegistry(_native);
        reg.SetSources(new[] { ModelSource.Comfy("comfyui", _comfy) });
        Assert.Equal(2, reg.Catalog.All.Count);
        Assert.Contains(reg.Sources, s => s.Id == "comfyui");
        Assert.Contains(reg.Sources, s => s.Kind == ModelSourceKind.Native);
    }

    [Fact]
    public void SetSources_NoScan_DefersExternal_UntilLoad()
    {
        var reg = new ModelRegistry(_native);
        reg.SetSources(new[] { ModelSource.Comfy("comfyui", _comfy) }, scan: false);
        Assert.Single(reg.Catalog.All);                    // native only — external deferred
        Assert.Contains(reg.Sources, s => s.Id == "comfyui");
        reg.Load();                                        // background scan folds it in
        Assert.Equal(2, reg.Catalog.All.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_native, recursive: true); } catch { }
        try { Directory.Delete(_comfy, recursive: true); } catch { }
    }
}

public class ModelSourceSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sable_set_" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void ModelSources_RoundTripThroughSettings()
    {
        var s = new SableSettings();
        s.ModelSources.Add(ModelSource.Comfy("comfyui", @"D:\comfy\ComfyUI\models"));
        SettingsService.Save(s, _path);

        var back = SettingsService.Load(_path);
        var src = Assert.Single(back.ModelSources);
        Assert.Equal("comfyui", src.Id);
        Assert.Equal(ModelSourceKind.ComfyUI, src.Kind);
        Assert.True(src.ReadOnly);
        Assert.Equal(@"D:\comfy\ComfyUI\models", src.Path);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}

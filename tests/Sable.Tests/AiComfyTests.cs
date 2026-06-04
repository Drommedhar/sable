using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sable.Ai.Comfy.Ipc;
using Sable.Ai.Comfy.Provisioning;
using Sable.Ai.Sidecar.Provisioning;
using Sable.Ai.Comfy.Workflow;
using Sable.Core.Ai;
using Xunit;

namespace Sable.Tests;

public class WorkflowBuilderTests
{
    private static readonly ComfyModelRef Sdxl = new("SDXL", ComfyModelKind.Checkpoint, "sdxl_base.safetensors");

    private static JsonElement Parse(IReadOnlyDictionary<string, object> g)
        => JsonDocument.Parse(WorkflowBuilder.ToJson(g)).RootElement;

    [Fact]
    public void Txt2Img_HasCheckpointSamplerDecodeSave()
    {
        var g = WorkflowBuilder.Txt2Img(new GenRequest("m", AiTaskKind.Txt2Img, "a fox", "blurry", Steps: 30, Cfg: 6.5, Seed: 42), Sdxl, 1024, 768);
        var root = Parse(g);

        Assert.Equal("CheckpointLoaderSimple", root.GetProperty("ckpt").GetProperty("class_type").GetString());
        Assert.Equal("sdxl_base.safetensors", root.GetProperty("ckpt").GetProperty("inputs").GetProperty("ckpt_name").GetString());

        var sampler = root.GetProperty("sampler").GetProperty("inputs");
        Assert.Equal(30, sampler.GetProperty("steps").GetInt32());
        Assert.Equal(6.5, sampler.GetProperty("cfg").GetDouble());
        Assert.Equal(42, sampler.GetProperty("seed").GetInt64());

        Assert.Equal("a fox", root.GetProperty("pos").GetProperty("inputs").GetProperty("text").GetString());
        Assert.Equal("blurry", root.GetProperty("neg").GetProperty("inputs").GetProperty("text").GetString());
        var latent = root.GetProperty("latent").GetProperty("inputs");
        Assert.Equal(1024, latent.GetProperty("width").GetInt32());
        Assert.Equal(768, latent.GetProperty("height").GetInt32());
        Assert.Equal("SaveImage", root.GetProperty("save").GetProperty("class_type").GetString());
    }

    [Fact]
    public void Txt2Img_LoraChain_LinksModelAndClip()
    {
        var req = new GenRequest("m", AiTaskKind.Txt2Img, "x", Loras: new[] { new AdapterRef("lid", 0.8) });
        var g = WorkflowBuilder.Txt2Img(req, Sdxl, 512, 512, loraName: _ => "detail.safetensors");
        var root = Parse(g);

        var lora = root.GetProperty("lora0").GetProperty("inputs");
        Assert.Equal("detail.safetensors", lora.GetProperty("lora_name").GetString());
        Assert.Equal(0.8, lora.GetProperty("strength_model").GetDouble());
        // the checkpoint feeds the lora...
        Assert.Equal("ckpt", lora.GetProperty("model")[0].GetString());
        // ...and the sampler reads the lora's MODEL output, not the checkpoint's
        Assert.Equal("lora0", root.GetProperty("sampler").GetProperty("inputs").GetProperty("model")[0].GetString());
        // CLIPTextEncode reads the lora's CLIP output (index 1)
        Assert.Equal("lora0", root.GetProperty("pos").GetProperty("inputs").GetProperty("clip")[0].GetString());
        Assert.Equal(1, root.GetProperty("pos").GetProperty("inputs").GetProperty("clip")[1].GetInt32());
    }

    [Theory]
    [InlineData(@"D:\comfy\models\checkpoints\sdxl.safetensors", ComfyModelKind.Checkpoint)]
    [InlineData(@"D:\comfy\models\diffusion_models\flux2.safetensors", ComfyModelKind.Unet)]
    [InlineData("D:/comfy/models/diffusion_models/sub/qwen.safetensors", ComfyModelKind.Unet)]   // nested
    [InlineData("D:/comfy/models/unet/flux.safetensors", ComfyModelKind.Unet)]
    [InlineData("D:/comfy/models/checkpoints/sd15/model.safetensors", ComfyModelKind.Checkpoint)] // nested checkpoint
    public void KindForPath_Classifies(string path, ComfyModelKind expect)
        => Assert.Equal(expect, WorkflowBuilder.KindForPath(path));

    [Fact]
    public void ComfyName_RelativeToTypeFolder()
    {
        // nested under diffusion_models/Qwen → ComfyUI lists "Qwen<sep>file"
        var n = WorkflowBuilder.ComfyName(@"D:\comfy\models\diffusion_models\Qwen\qwen_image.safetensors");
        Assert.Equal("Qwen" + System.IO.Path.DirectorySeparatorChar + "qwen_image.safetensors", n);
        // flat → just the filename
        Assert.Equal("sdxl.safetensors", WorkflowBuilder.ComfyName("D:/comfy/models/checkpoints/sdxl.safetensors"));
    }

    [Fact]
    public void Inpaint_UsesLoadImageAndVAEEncodeForInpaint()
    {
        var g = WorkflowBuilder.Inpaint(new GenRequest("m", AiTaskKind.Inpaint, "fill it"), Sdxl, "up.png", 512, 512, denoise: 0.75);
        var root = Parse(g);
        Assert.Equal("up.png", root.GetProperty("image").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal("VAEEncodeForInpaint", root.GetProperty("encode").GetProperty("class_type").GetString());
        Assert.Equal("image", root.GetProperty("encode").GetProperty("inputs").GetProperty("mask")[0].GetString());
        Assert.Equal(0.75, root.GetProperty("sampler").GetProperty("inputs").GetProperty("denoise").GetDouble());
    }

    [Fact]
    public void Unet_AssembledUsesUnetClipVaeLoaders()
    {
        var flux = new ComfyModelRef("Flux", ComfyModelKind.Unet, "flux2-dev.safetensors",
            ClipNames: new[] { "clip_l.safetensors", "t5xxl.safetensors" }, VaeName: "flux2-vae.safetensors");
        var g = WorkflowBuilder.Txt2Img(new GenRequest("m", AiTaskKind.Txt2Img, "x"), flux, 1024, 1024);
        var root = Parse(g);
        Assert.Equal("UNETLoader", root.GetProperty("unet").GetProperty("class_type").GetString());
        Assert.Equal("flux2-dev.safetensors", root.GetProperty("unet").GetProperty("inputs").GetProperty("unet_name").GetString());
        Assert.Equal("DualCLIPLoader", root.GetProperty("clip").GetProperty("class_type").GetString());
        Assert.Equal("flux", root.GetProperty("clip").GetProperty("inputs").GetProperty("type").GetString());
        Assert.Equal("VAELoader", root.GetProperty("vae").GetProperty("class_type").GetString());
    }
}

public class ComfyWsParseTests
{
    [Fact]
    public void Progress()
    {
        var e = ComfyClient.ParseEvent(@"{""type"":""progress"",""data"":{""value"":7,""max"":20}}");
        Assert.Equal(ComfyEventKind.Progress, e.Kind);
        Assert.Equal(7, e.Value);
        Assert.Equal(20, e.Max);
    }

    [Fact]
    public void Executing_Node()
    {
        var e = ComfyClient.ParseEvent(@"{""type"":""executing"",""data"":{""node"":""3""}}");
        Assert.Equal(ComfyEventKind.Executing, e.Kind);
        Assert.Equal("3", e.Node);
    }

    [Fact]
    public void Executing_NullNode_MeansDone()
    {
        var e = ComfyClient.ParseEvent(@"{""type"":""executing"",""data"":{""node"":null}}");
        Assert.Equal(ComfyEventKind.Executing, e.Kind);
        Assert.Null(e.Node);
    }

    [Fact]
    public void Executed_Images()
    {
        var e = ComfyClient.ParseEvent(
            @"{""type"":""executed"",""data"":{""node"":""9"",""output"":{""images"":[{""filename"":""sable_0001.png"",""subfolder"":"""",""type"":""output""}]}}}");
        Assert.Equal(ComfyEventKind.Executed, e.Kind);
        var img = Assert.Single(e.Images!);
        Assert.Equal("sable_0001.png", img.Filename);
        Assert.Equal("output", img.Type);
    }

    [Fact]
    public void ExecutionError_Parsed()
    {
        var e = ComfyClient.ParseEvent(@"{""type"":""execution_error"",""data"":{""node_type"":""UNETLoader"",""exception_message"":""file not found""}}");
        Assert.Equal(ComfyEventKind.Error, e.Kind);
        Assert.Contains("UNETLoader", e.Message);
        Assert.Contains("file not found", e.Message);
    }

    [Fact]
    public void Garbage_IsOther()
        => Assert.Equal(ComfyEventKind.Other, ComfyClient.ParseEvent("not json").Kind);
}

public class ArchTemplatesTests
{
    [Theory]
    [InlineData("Flux", "flux")]
    [InlineData("Qwen", "qwen_image")]
    [InlineData("SD3", "sd3")]
    [InlineData("whatever", "stable_diffusion")]
    public void ClipType(string family, string expect) => Assert.Equal(expect, ArchTemplates.ClipType(family));

    [Theory]
    [InlineData("LTX", false)]
    [InlineData("Wan", false)]
    [InlineData("SDXL", true)]
    [InlineData("Qwen", true)]
    public void IsImageArch(string family, bool expect) => Assert.Equal(expect, ArchTemplates.IsImageArch(family));
}

public class ComfyReuseTests
{
    [Fact]
    public void ExtraModelPaths_PointsAtUserModels()
    {
        var y = ComfyReuse.BuildExtraModelPaths(@"D:\comfy\ComfyUI\models");
        Assert.Contains("base_path: D:/comfy/ComfyUI/models", y);   // backslashes normalised
        Assert.Contains("checkpoints: checkpoints", y);
        Assert.Contains("diffusion_models: diffusion_models", y);
    }

    [Fact]
    public void CustomNodeLinks_SkipNoiseAndPair()
    {
        var links = ComfyReuse.PlanCustomNodeLinks(@"C:\u\custom_nodes",
            new[] { "ComfyUI-Manager", "__pycache__", ".git", "MyNode" }, @"C:\own\custom_nodes");
        Assert.Equal(2, links.Count);   // Manager + MyNode; pycache/.git skipped
        Assert.Contains(links, l => l.Src.EndsWith("ComfyUI-Manager") && l.Dst.EndsWith("ComfyUI-Manager"));
    }
}

public class ComfyLocatorTests
{
    [Fact]
    public void ForeignOsComfy_OnWindows_NotLocated()
    {
        // a Linux ComfyUI: main.py + a posix bin/python exist, but the host is Windows → reject (→ provision own)
        bool Exists(string p)
        {
            var n = p.Replace('\\', '/');
            return n.EndsWith("/main.py") || n.EndsWith("/venv/bin/python");
        }
        Assert.Null(ComfyLocator.LocateSameOs(@"X:\comfy\ComfyUI\models", HostOs.Windows, Exists));
    }

    [Fact]
    public void SameOsComfy_Located()
    {
        bool Exists(string p)
        {
            var n = p.Replace('\\', '/');
            return n.EndsWith("/main.py") || n.EndsWith("/venv/Scripts/python.exe");
        }
        var install = ComfyLocator.LocateSameOs(@"X:\comfy\ComfyUI\models", HostOs.Windows, Exists);
        Assert.NotNull(install);
        Assert.EndsWith("ComfyUI", install!.ComfyDir.Replace('\\', '/'));
    }
}

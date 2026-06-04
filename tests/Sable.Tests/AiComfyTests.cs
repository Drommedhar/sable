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
}

public class WorkflowTemplateTests
{
    // a synthetic API-format graph with a conditioning passthrough between KSampler and the positive encoder
    private const string Api = @"{
      ""1"": { ""class_type"":""LoadImage"", ""inputs"":{ ""image"":""old.png"" } },
      ""2"": { ""class_type"":""CLIPTextEncode"", ""_meta"":{""title"":""Positive Prompt""}, ""inputs"":{ ""text"":""old pos"", ""clip"":[""5"",0] } },
      ""3"": { ""class_type"":""CLIPTextEncode"", ""_meta"":{""title"":""Negative Prompt""}, ""inputs"":{ ""text"":""old neg"", ""clip"":[""5"",0] } },
      ""8"": { ""class_type"":""FluxKontextMultiReferenceLatentMethod"", ""inputs"":{ ""conditioning"":[""2"",0], ""reference_latents_method"":""x"" } },
      ""4"": { ""class_type"":""KSampler"", ""inputs"":{ ""model"":[""6"",0], ""positive"":[""8"",0], ""negative"":[""3"",0], ""latent_image"":[""7"",0], ""seed"":1, ""steps"":10, ""cfg"":5.0, ""denoise"":1.0 } }
    }";

    [Fact]
    public void Apply_InjectsImagePromptAndSamplerParams()
    {
        var outJson = WorkflowTemplate.Apply(Api, new WorkflowTemplate.Inject("new pos", "new neg", "new.png", 42, 20, 2.5, 0.8));
        var root = JsonDocument.Parse(outJson).RootElement;
        Assert.Equal("new.png", root.GetProperty("1").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal("new pos", root.GetProperty("2").GetProperty("inputs").GetProperty("text").GetString());   // traced through node 8
        Assert.Equal("new neg", root.GetProperty("3").GetProperty("inputs").GetProperty("text").GetString());
        var k = root.GetProperty("4").GetProperty("inputs");
        Assert.Equal(42, k.GetProperty("seed").GetInt64());
        Assert.Equal(20, k.GetProperty("steps").GetInt32());
        Assert.Equal(2.5, k.GetProperty("cfg").GetDouble());
        Assert.Equal(0.8, k.GetProperty("denoise").GetDouble());
    }

    private const string LoraApi = @"{
      ""5"": { ""class_type"":""UNETLoader"", ""inputs"":{ ""unet_name"":""u"" } },
      ""6"": { ""class_type"":""LoraLoaderModelOnly"", ""inputs"":{ ""model"":[""5"",0], ""lora_name"":""baked.safetensors"", ""strength_model"":1.0 } },
      ""4"": { ""class_type"":""KSampler"", ""inputs"":{ ""model"":[""6"",0], ""positive"":[""2"",0], ""negative"":[""3"",0], ""latent_image"":[""7"",0], ""seed"":1, ""steps"":10, ""cfg"":5.0, ""denoise"":1.0 } }
    }";

    [Fact]
    public void ModelLoaders_OverriddenByPreset()
    {
        const string api = @"{
          ""5"": { ""class_type"":""UNETLoader"", ""inputs"":{ ""unet_name"":""Flux/old.safetensors"", ""weight_dtype"":""default"" } },
          ""6"": { ""class_type"":""DualCLIPLoader"", ""inputs"":{ ""clip_name1"":""old1"", ""clip_name2"":""old2"", ""type"":""flux"" } },
          ""7"": { ""class_type"":""VAELoader"", ""inputs"":{ ""vae_name"":""oldvae"" } }
        }";
        var outJson = WorkflowTemplate.Apply(api, new WorkflowTemplate.Inject("p", "n", "i.png", 1, 10, 5, 1.0,
            UnetName: "flux2_dev.safetensors", ClipNames: new[] { "clip_l.sft", "t5.sft" }, VaeName: "flux2_vae.sft"));
        var root = JsonDocument.Parse(outJson).RootElement;
        Assert.Equal("flux2_dev.safetensors", root.GetProperty("5").GetProperty("inputs").GetProperty("unet_name").GetString());
        Assert.Equal("clip_l.sft", root.GetProperty("6").GetProperty("inputs").GetProperty("clip_name1").GetString());
        Assert.Equal("t5.sft", root.GetProperty("6").GetProperty("inputs").GetProperty("clip_name2").GetString());
        Assert.Equal("flux2_vae.sft", root.GetProperty("7").GetProperty("inputs").GetProperty("vae_name").GetString());
    }

    [Fact]
    public void NoLoraSelected_BypassesLoaderAndRewires()
    {
        var outJson = WorkflowTemplate.Apply(LoraApi, new WorkflowTemplate.Inject("p", "n", "i.png", 1, 10, 5, 1.0));
        var root = JsonDocument.Parse(outJson).RootElement;
        Assert.False(root.TryGetProperty("6", out _));   // loader removed
        // KSampler.model now points straight at the UNET (rewired around the bypassed loader)
        var m = root.GetProperty("4").GetProperty("inputs").GetProperty("model");
        Assert.Equal("5", m[0].GetString());
    }

    [Fact]
    public void ReadDefaults_FromFluxStylePrimitivesAndGuidance()
    {
        const string api = @"{
          ""90"": { ""class_type"":""PrimitiveInt"", ""_meta"":{""title"":""Steps""}, ""inputs"":{ ""value"":20 } },
          ""26"": { ""class_type"":""FluxGuidance"", ""inputs"":{ ""guidance"":4, ""conditioning"":[""6"",0] } }
        }";
        var (steps, cfg) = WorkflowTemplate.ReadDefaults(api);
        Assert.Equal(20, steps);
        Assert.Equal(4, cfg);
    }

    [Fact]
    public void PromptByTitle_InjectsIntoPositiveNode()
    {
        const string api = @"{
          ""6"": { ""class_type"":""CLIPTextEncode"", ""_meta"":{""title"":""CLIP Text Encode (Positive Prompt)""}, ""inputs"":{ ""text"":""old"", ""clip"":[""38"",0] } },
          ""13"": { ""class_type"":""SamplerCustomAdvanced"", ""inputs"":{ ""noise_seed"":1, ""guider"":[""22"",0] } }
        }";
        var outJson = WorkflowTemplate.Apply(api, new WorkflowTemplate.Inject("new prompt", "", "i.png", 7, 10, 5, 1.0));
        var root = JsonDocument.Parse(outJson).RootElement;
        Assert.Equal("new prompt", root.GetProperty("6").GetProperty("inputs").GetProperty("text").GetString());
        Assert.Equal(7, root.GetProperty("13").GetProperty("inputs").GetProperty("noise_seed").GetInt64());
    }

    [Fact]
    public void OptionalPatchNode_IsBypassed()
    {
        const string api = @"{
          ""5"": { ""class_type"":""UNETLoader"", ""inputs"":{ ""unet_name"":""u"" } },
          ""9"": { ""class_type"":""PatchSageAttentionKJ"", ""inputs"":{ ""model"":[""5"",0], ""sage_attention"":""auto"" } },
          ""4"": { ""class_type"":""KSampler"", ""inputs"":{ ""model"":[""9"",0], ""positive"":[""2"",0], ""negative"":[""3"",0], ""latent_image"":[""7"",0], ""seed"":1, ""steps"":10, ""cfg"":5.0, ""denoise"":1.0 } }
        }";
        var outJson = WorkflowTemplate.Apply(api, new WorkflowTemplate.Inject("p", "n", "i.png", 1, 10, 5, 1.0));
        var root = JsonDocument.Parse(outJson).RootElement;
        Assert.False(root.TryGetProperty("9", out _));   // sage-attention patch removed
        Assert.Equal("5", root.GetProperty("4").GetProperty("inputs").GetProperty("model")[0].GetString());   // rewired to UNET
    }

    [Fact]
    public void SelectedLora_IsAssigned()
    {
        var outJson = WorkflowTemplate.Apply(LoraApi, new WorkflowTemplate.Inject("p", "n", "i.png", 1, 10, 5, 1.0,
            new[] { ("mylora.safetensors", 0.8) }));
        var root = JsonDocument.Parse(outJson).RootElement;
        var li = root.GetProperty("6").GetProperty("inputs");
        Assert.Equal("mylora.safetensors", li.GetProperty("lora_name").GetString());
        Assert.Equal(0.8, li.GetProperty("strength_model").GetDouble());
        Assert.Equal("6", root.GetProperty("4").GetProperty("inputs").GetProperty("model")[0].GetString());   // kept
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

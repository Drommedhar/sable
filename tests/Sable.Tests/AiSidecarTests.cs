using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sable.Ai;
using Sable.Ai.Models;
using Sable.Ai.Sidecar;
using Sable.Ai.Sidecar.Ipc;
using Sable.Ai.Sidecar.Provisioning;
using Sable.Core.Ai;
using Sable.Engine;
using Sable.Engine.Layers;
using Xunit;

namespace Sable.Tests;

public class ComfyEnvLocatorTests
{
    [Fact]
    public void RootFrom_StripsModelsLeaf()
    {
        Assert.Equal(NormalizeSep(@"X:\comfy\ComfyUI"),
            NormalizeSep(ComfyEnvLocator.RootFrom(@"X:\comfy\ComfyUI\models")));
        // not a models folder → unchanged
        Assert.Equal(NormalizeSep(@"X:\comfy\ComfyUI"),
            NormalizeSep(ComfyEnvLocator.RootFrom(@"X:\comfy\ComfyUI")));
    }

    [Fact]
    public void Candidates_IncludeBothOsFamilies()
    {
        var cands = ComfyEnvLocator.Candidates(@"X:\comfy\ComfyUI\models");
        Assert.Contains(cands, c => c.Kind == EnvOsKind.Windows && c.Path.EndsWith("python.exe"));
        Assert.Contains(cands, c => c.Kind == EnvOsKind.Posix && c.Path.EndsWith("python"));
        Assert.Contains(cands, c => c.Path.Contains("python_embeded"));
    }

    [Fact]
    public void HostGate_RejectsPosixVenvOnWindows()
    {
        var posix = new EnvCandidate("/comfy/venv/bin/python", EnvOsKind.Posix);
        Assert.False(ComfyEnvLocator.HostCompatible(posix, HostOs.Windows));   // the Linux-on-Windows case
        Assert.True(ComfyEnvLocator.HostCompatible(posix, HostOs.Linux));
        Assert.True(ComfyEnvLocator.HostCompatible(posix, HostOs.MacOS));

        var win = new EnvCandidate(@"C:\comfy\venv\Scripts\python.exe", EnvOsKind.Windows);
        Assert.True(ComfyEnvLocator.HostCompatible(win, HostOs.Windows));
        Assert.False(ComfyEnvLocator.HostCompatible(win, HostOs.Linux));
    }

    private static string NormalizeSep(string s) => s.Replace('\\', '/');
}

public class EnvCapsTests
{
    private static EnvCaps Caps(string torch = "2.4.0", string diff = "0.30.0", bool cuda = true)
        => new(torch, diff, cuda ? "12.4" : null, cuda, Mps: false, Rocm: false, DirectMl: false);

    [Fact]
    public void Usable_WhenTorchDiffusersAndAccelerator()
        => Assert.True(Caps().IsUsable(out _));

    [Fact]
    public void NotUsable_WhenCpuOnly()
    {
        Assert.False(Caps(cuda: false).IsUsable(out var why));
        Assert.Contains("accelerator", why);
    }

    [Fact]
    public void NotUsable_WhenDiffusersMissing()
        => Assert.False(Caps(diff: "").IsUsable(out _));

    [Fact]
    public void NotUsable_WhenDiffusersTooOld()
    {
        Assert.False(Caps(diff: "0.10.0").IsUsable(out var why));
        Assert.Contains("0.10.0", why);
    }

    [Theory]
    [InlineData("0.30.0", "0.27.0", 1)]
    [InlineData("0.27.0", "0.27.0", 0)]
    [InlineData("0.26.9", "0.27.0", -1)]
    [InlineData("2.4.0+cu124", "2.0.0", 1)]   // non-numeric tail ignored
    public void CompareVersions(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(EnvCaps.CompareVersions(a, b)));
}

public class EnvResolverTests
{
    private sealed class FakeProbe : IEnvProbe
    {
        public Dictionary<string, EnvCaps?> ByPath = new();
        public List<string> Probed = new();
        public Task<EnvCaps?> ProbeAsync(string pythonExe, CancellationToken ct = default)
        {
            Probed.Add(pythonExe);
            return Task.FromResult(ByPath.TryGetValue(pythonExe, out var c) ? c : null);
        }
    }

    private static EnvCaps Good => new("2.4.0", "0.30.0", "12.4", true, false, false, false);

    [Fact]
    public async Task PrefersPinned()
    {
        var probe = new FakeProbe { ByPath = { ["C:/py/python.exe"] = Good } };
        var env = await EnvResolver.ResolveAsync(
            new EnvResolveOptions("C:/py/python.exe", null, null, HostOs.Windows, Exists: _ => true), probe);
        Assert.NotNull(env);
        Assert.Equal("pinned", env!.Origin);
    }

    [Fact]
    public async Task LinuxComfyVenv_OnWindows_IsSkipped_FallsToOwn()
    {
        // a Linux ComfyUI: only the posix bin/python "exists"; the own venv is usable.
        var ownPy = @"C:\Users\me\AppData\Roaming\Sable\sidecar\venv\Scripts\python.exe";
        var probe = new FakeProbe { ByPath = { [ownPy] = Good } };
        // mark the posix comfy python as "existing" too, to prove the GATE (not existence) skips it
        bool Exists(string p) => p == ownPy || p.Replace('\\', '/').EndsWith("/venv/bin/python");

        var env = await EnvResolver.ResolveAsync(
            new EnvResolveOptions(null, @"X:\comfy\ComfyUI\models", ownPy, HostOs.Windows, Exists),
            probe);

        Assert.NotNull(env);
        Assert.Equal("sable", env!.Origin);
        // the foreign-OS posix python must NEVER have been probed
        Assert.DoesNotContain(probe.Probed, p => p.Replace('\\', '/').EndsWith("/venv/bin/python"));
    }

    [Fact]
    public async Task UsesComfyVenv_WhenHostMatchesAndUsable()
    {
        var comfyPy = @"X:\comfy\ComfyUI\venv\Scripts\python.exe";
        var probe = new FakeProbe { ByPath = { [comfyPy] = Good } };
        var env = await EnvResolver.ResolveAsync(
            new EnvResolveOptions(null, @"X:\comfy\ComfyUI\models", null, HostOs.Windows, Exists: p => p == comfyPy),
            probe);
        Assert.NotNull(env);
        Assert.Equal("comfyui", env!.Origin);
    }

    [Fact]
    public async Task ReturnsNull_WhenNothingUsable()
    {
        var env = await EnvResolver.ResolveAsync(
            new EnvResolveOptions(null, null, null, HostOs.Windows, Exists: _ => false), new FakeProbe());
        Assert.Null(env);
    }
}

public class EnvProbeParseTests
{
    [Fact]
    public void Parse_FullCudaJson()
    {
        const string json = @"{""os"":""Windows"",""torch"":""2.4.0+cu124"",""diffusers"":""0.30.0"",""cuda"":""12.4"",""cuda_avail"":true,""mps"":false,""rocm"":false,""directml"":false}";
        var caps = EnvProbe.Parse(json);
        Assert.NotNull(caps);
        Assert.Equal("2.4.0+cu124", caps!.TorchVersion);
        Assert.Equal("0.30.0", caps.DiffusersVersion);
        Assert.True(caps.Cuda);
        Assert.Equal("12.4", caps.CudaVersion);
    }

    [Fact]
    public void Parse_ToleratesWarningLinesBeforeJson()
    {
        const string json = "UserWarning: blah\n{\"torch\":\"\",\"diffusers\":\"\",\"cuda_avail\":false}";
        var caps = EnvProbe.Parse(json);
        Assert.NotNull(caps);
        Assert.Equal("", caps!.TorchVersion);
        Assert.False(caps.IsUsable(out _));
    }

    [Fact]
    public void Parse_GarbageIsNull()
        => Assert.Null(EnvProbe.Parse("no json here"));
}

public class UvEnvTests
{
    [Fact]
    public void TorchArgs_PickCudaIndex()
    {
        var args = UvEnv.TorchInstallArgs(TorchVendor.Cuda);
        Assert.Contains("--index-url", args);
        Assert.Contains(args, a => a.Contains("download.pytorch.org/whl/cu"));
    }

    [Fact]
    public void TorchArgs_DirectMlAddsPackage()
        => Assert.Contains("torch-directml", UvEnv.TorchInstallArgs(TorchVendor.DirectMl));

    [Fact]
    public void PythonIn_MatchesHostLayout()
    {
        var py = UvEnv.PythonIn("/x/venv");
        Assert.True(py.EndsWith("python.exe") || py.EndsWith(Path.Combine("bin", "python")));
    }
}

public class LoadPlanTests
{
    private static ModelManifest Checkpoint(string id, string family, string file) => new()
    {
        Id = id, Name = id, Kind = ModelKind.Base, Family = family, Tier = AiTier.Generative,
        Tasks = new[] { AiTaskKind.Txt2Img }, Files = new[] { file },
    };

    private static ModelManifest Component(string id, string family) => new()
    {
        Id = id, Name = id, Kind = ModelKind.Component, ComponentFamily = family,
        Files = new[] { $@"X:\{id}.safetensors" },
    };

    [Fact]
    public void SingleFileCheckpoint_FromComfyDraft()
    {
        var m = Checkpoint("c:sdxl", "SDXL", @"X:\ComfyUI\models\checkpoints\sdxl.safetensors");
        var cat = new ModelCatalog(new[] { m });
        var r = LoadPlan.Resolve(cat, m);
        Assert.True(r.Ok);
        Assert.Equal(PipelineKind.SingleFile, r.Request!.Kind);
        Assert.Equal(@"X:\ComfyUI\models\checkpoints\sdxl.safetensors", r.Request.Paths.Checkpoint);
    }

    [Fact]
    public void PretrainedDir_WhenNoExtension()
    {
        var m = Checkpoint("c:folder", "SDXL", @"X:\models\sdxl-diffusers");
        var r = LoadPlan.Resolve(new ModelCatalog(new[] { m }), m);
        Assert.Equal(PipelineKind.Pretrained, r.Request!.Kind);
        Assert.Equal(@"X:\models\sdxl-diffusers", r.Request.Paths.PretrainedDir);
    }

    [Fact]
    public void Assembled_ResolvesSharedComponents()
    {
        var t5 = Component("t5", "T5-XXL");
        var clip = Component("clipl", "CLIP-L");
        var vae = Component("vae", "VAE-Flux");
        var flux = new ModelManifest
        {
            Id = "flux", Name = "flux", Kind = ModelKind.Base, Family = "Flux", Tier = AiTier.Generative,
            Tasks = new[] { AiTaskKind.Txt2Img },
            AcceptsTextEncoders = new[] { "CLIP-L", "T5-XXL" },
            Components = new ModelComponents
            {
                Denoiser = new ComponentSource { Path = @"X:\unet\flux1.safetensors" },
                TextEncoders = new[] { new ComponentSource { Ref = "clipl" }, new ComponentSource { Ref = "t5" } },
                Vae = new ComponentSource { Ref = "vae" },
            },
        };
        var cat = new ModelCatalog(new[] { flux, t5, clip, vae });

        var r = LoadPlan.Resolve(cat, flux);
        Assert.True(r.Ok);
        Assert.Equal(PipelineKind.Assembled, r.Request!.Kind);
        Assert.Equal(@"X:\unet\flux1.safetensors", r.Request.Paths.Denoiser);
        Assert.Equal(2, r.Request.Paths.TextEncoders!.Count);
        Assert.Equal(@"X:\vae.safetensors", r.Request.Paths.Vae);
    }

    [Fact]
    public void Assembled_MissingEncoder_Blocks()
    {
        var flux = new ModelManifest
        {
            Id = "flux", Name = "flux", Kind = ModelKind.Base, Family = "Flux", Tier = AiTier.Generative,
            AcceptsTextEncoders = new[] { "CLIP-L", "T5-XXL" },
            Components = new ModelComponents
            {
                Denoiser = new ComponentSource { Path = @"X:\unet\flux1.safetensors" },
                TextEncoders = new[] { new ComponentSource { Ref = "clipl" }, new ComponentSource { Ref = "t5-missing" } },
            },
        };
        var clip = Component("clipl", "CLIP-L");
        var r = LoadPlan.Resolve(new ModelCatalog(new[] { flux, clip }), flux);
        Assert.False(r.Ok);
        Assert.Contains("t5-missing", r.Missing);
    }

    [Fact]
    public void Loras_ResolvedToPathsAndWeights()
    {
        var ck = Checkpoint("c:sdxl", "SDXL", @"X:\sdxl.safetensors");
        var lora = new ModelManifest { Id = "lora1", Kind = ModelKind.Adapter, AdapterType = AdapterType.Lora, Files = new[] { @"X:\loras\detail.safetensors" } };
        var cat = new ModelCatalog(new[] { ck, lora });
        var r = LoadPlan.Resolve(cat, ck, offload: true, loras: new[] { new AdapterRef("lora1", 0.8) });
        Assert.True(r.Request!.Offload);
        var spec = Assert.Single(r.Request.Loras!);
        Assert.Equal(@"X:\loras\detail.safetensors", spec.Path);
        Assert.Equal(0.8, spec.Weight);
    }

    [Fact]
    public void NonBase_Rejected()
    {
        var lora = new ModelManifest { Id = "l", Kind = ModelKind.Adapter };
        Assert.False(LoadPlan.Resolve(new ModelCatalog(new[] { lora }), lora).Ok);
    }

    [Fact]
    public void LoadModelRequest_RoundTripsWithStringEnum()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var req = new LoadModelRequest("m", "SDXL", PipelineKind.SingleFile,
            new ComponentPaths(Checkpoint: "x.safetensors"), Offload: true,
            Loras: new[] { new LoraSpec("l.safetensors", 0.7, "lora1") });
        var json = JsonSerializer.Serialize(req, opts);
        Assert.Contains("SingleFile", json);   // enum as string (the Python server compares strings)
        var back = JsonSerializer.Deserialize<LoadModelRequest>(json, opts)!;
        Assert.Equal(PipelineKind.SingleFile, back.Kind);
        Assert.Equal("x.safetensors", back.Paths.Checkpoint);
        Assert.True(back.Offload);
        Assert.Equal(0.7, back.Loras!.Single().Weight);
    }
}

public class GenerativeFillTests
{
    private sealed class FakeGen : IGenerativeBackend
    {
        public bool IsAvailable { get; set; } = true;
        public int LoadCount;
        public LoadModelRequest? LastLoad;
        public GenRequest? LastGen;
        public Func<GenRequest, AiImage>? OnGen;

        public Task<LoadModelResult> LoadModelAsync(LoadModelRequest req, CancellationToken ct = default)
        { LoadCount++; LastLoad = req; return Task.FromResult(new LoadModelResult(true, 1_000_000)); }

        public Task<AiImage> GenerateAsync(GenRequest req, CancellationToken ct = default)
        {
            LastGen = req;
            int w = req.Image?.Width ?? 8, h = req.Image?.Height ?? 8;
            return Task.FromResult(OnGen?.Invoke(req) ?? new AiImage(new byte[w * h * 4], w, h));
        }
    }

    private static ModelRegistry RegistryWithInpaintBase()
    {
        var reg = new ModelRegistry(Path.Combine(Path.GetTempPath(), "gen_" + Guid.NewGuid().ToString("N")));
        reg.Catalog.Add(new ModelManifest
        {
            Id = "sdxl-inp", Name = "SDXL", Kind = ModelKind.Base, Family = "SDXL", Tier = AiTier.Generative,
            Tasks = new[] { AiTaskKind.Inpaint, AiTaskKind.Txt2Img }, Files = new[] { @"X:\sdxl.safetensors" },
        });
        return reg;
    }

    [Fact]
    public async Task GenerativeFill_LoadsModel_AddsClippedLayerAboveSource()
    {
        var svc = new AiService(RegistryWithInpaintBase()) { Generative = new FakeGen() };
        var fake = (FakeGen)svc.Generative!;

        var doc = new Document(8, 8);
        var target = new PixelLayer(8, 8, "base");
        doc.Layers.Add(target);
        var region = new AiMask(new byte[8 * 8], 8, 8);

        var cmd = await svc.GenerativeFillAsync(doc, target, region, new GenRequest("", AiTaskKind.Inpaint, "a cat"));
        cmd.Do();

        Assert.Equal(2, doc.Layers.Count);
        Assert.Equal(0, doc.Layers.IndexOf(target));    // base stays at bottom
        var added = doc.Layers[1];                      // new fill layer sits above it
        Assert.Contains("fill", added.Name);
        Assert.True(added.HasMask);                              // clipped to the fill region
        Assert.Equal(1, fake.LoadCount);
        Assert.Equal(PipelineKind.SingleFile, fake.LastLoad!.Kind);
        Assert.Equal(AiTaskKind.Inpaint, fake.LastGen!.Task);
    }

    [Fact]
    public async Task EnsureModelLoaded_SkipsReload_ForSameStack()
    {
        var svc = new AiService(RegistryWithInpaintBase()) { Generative = new FakeGen() };
        var fake = (FakeGen)svc.Generative!;
        await svc.EnsureModelLoadedAsync("sdxl-inp", offload: false, loras: null);
        await svc.EnsureModelLoadedAsync("sdxl-inp", offload: false, loras: null);
        Assert.Equal(1, fake.LoadCount);
        await svc.EnsureModelLoadedAsync("sdxl-inp", offload: true, loras: null);   // different stack → reload
        Assert.Equal(2, fake.LoadCount);
    }

    [Fact]
    public async Task EnsureModelLoaded_MissingComponent_Throws()
    {
        var reg = new ModelRegistry(Path.Combine(Path.GetTempPath(), "gen_" + Guid.NewGuid().ToString("N")));
        reg.Catalog.Add(new ModelManifest
        {
            Id = "flux", Kind = ModelKind.Base, Family = "Flux", Tier = AiTier.Generative,
            Tasks = new[] { AiTaskKind.Inpaint }, AcceptsTextEncoders = new[] { "CLIP-L", "T5-XXL" },
            Components = new ModelComponents
            {
                Denoiser = new ComponentSource { Path = @"X:\flux.safetensors" },
                TextEncoders = new[] { new ComponentSource { Ref = "t5-missing" } },
            },
        });
        var svc = new AiService(reg) { Generative = new FakeGen() };
        var ex = await Assert.ThrowsAsync<AiNotReadyException>(
            () => svc.EnsureModelLoadedAsync("flux", false, null));
        Assert.Contains("t5-missing", ex.Message);
    }

    [Fact]
    public async Task GenerativeFill_NoBackend_Throws()
    {
        var svc = new AiService(RegistryWithInpaintBase());   // Generative not set
        var doc = new Document(8, 8);
        var target = new PixelLayer(8, 8, "b");
        doc.Layers.Add(target);
        await Assert.ThrowsAsync<AiNotReadyException>(() =>
            svc.GenerativeFillAsync(doc, target, new AiMask(new byte[64], 8, 8), new GenRequest("", AiTaskKind.Inpaint, "x")));
    }
}

public class SidecarClientTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly string _token = "tok123";
    private readonly CancellationTokenSource _cts = new();

    public SidecarClientTests()
    {
        int port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            var auth = ctx.Request.Headers["Authorization"];
            string body; int code = 200;
            if (auth != $"Bearer {_token}") { code = 401; body = "{\"error\":\"unauthorized\"}"; }
            else if (ctx.Request.Url!.AbsolutePath.TrimEnd('/') == "/health")
                body = "{\"ok\":true,\"version\":\"test\",\"device\":\"cuda\"}";
            else if (ctx.Request.Url!.AbsolutePath.TrimEnd('/') == "/vram")
                body = "{\"totalBytes\":16000000000,\"freeBytes\":12000000000,\"device\":\"cuda\"}";
            else if (ctx.Request.Url!.AbsolutePath.TrimEnd('/') == "/load_model")
            {
                // echo back a success with the kind the client sent, proving enum-as-string on the wire
                using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var sent = await sr.ReadToEndAsync();
                bool sawString = sent.Contains("\"SingleFile\"");
                body = $"{{\"ok\":{(sawString ? "true" : "false")},\"peakVramBytes\":7000000000,\"device\":\"cuda\"}}";
            }
            else { code = 404; body = "{}"; }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    [Fact]
    public async Task Health_Ok_WithToken()
    {
        using var c = new SidecarClient(new Uri(_prefix), _token);
        var h = await c.HealthAsync();
        Assert.True(h.Ok);
        Assert.Equal("cuda", h.Device);
    }

    [Fact]
    public async Task Vram_ParsesReport()
    {
        using var c = new SidecarClient(new Uri(_prefix), _token);
        var v = await c.VramAsync();
        Assert.Equal(16_000_000_000, v.TotalBytes);
        Assert.Equal(12_000_000_000, v.FreeBytes);
    }

    [Fact]
    public async Task Health_NotOk_WithBadToken()
    {
        using var c = new SidecarClient(new Uri(_prefix), "wrong");
        var h = await c.HealthAsync();
        Assert.False(h.Ok);
    }

    [Fact]
    public async Task WaitHealthy_ReturnsTrue()
    {
        using var c = new SidecarClient(new Uri(_prefix), _token);
        Assert.True(await c.WaitHealthyAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LoadModel_SendsStringEnum_AndParsesResult()
    {
        using var c = new SidecarClient(new Uri(_prefix), _token);
        var req = new LoadModelRequest("m", "SDXL", PipelineKind.SingleFile, new ComponentPaths(Checkpoint: "x.safetensors"));
        var r = await c.LoadModelAsync(req);
        Assert.True(r.Ok);                       // server only returns ok if it saw "SingleFile" as a string
        Assert.Equal(7_000_000_000, r.PeakVramBytes);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

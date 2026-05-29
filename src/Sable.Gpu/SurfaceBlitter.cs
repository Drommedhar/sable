using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace Sable.Gpu;

/// <summary>
/// Presents a source texture into a render target with a fullscreen-triangle pass
/// (fullscreen_blit.wgsl). Used to blit the compositor's output into the swapchain.
/// Pipeline + sampler are built once for a given target format; the bind group is
/// rebuilt per call since the source view may change (e.g. on resize).
/// </summary>
public sealed unsafe class SurfaceBlitter : IDisposable
{
    private readonly WgpuDevice _gpu;
    private Sampler* _sampler;
    private BindGroupLayout* _bgl;
    private RenderPipeline* _pipeline;
    private Silk.NET.WebGPU.Buffer* _vpBuf;
    private Texture* _dummyMask;          // 1×1 R8 bound when there is no mask selection
    private TextureView* _dummyMaskView;

    public SurfaceBlitter(WgpuDevice gpu, TextureFormat targetFormat)
    {
        _gpu = gpu;
        var api = gpu.Api;

        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Nearest,   // crisp raw pixels when zoomed in
            MinFilter = FilterMode.Linear,    // smooth when zoomed out
            MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0,
            LodMaxClamp = 1,
            MaxAnisotropy = 1
        };
        _sampler = api.DeviceCreateSampler(gpu.Device, in samplerDesc);

        var bglEntries = stackalloc BindGroupLayoutEntry[4];
        bglEntries[0] = new BindGroupLayoutEntry
        {
            Binding = 0, Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D
            }
        };
        bglEntries[1] = new BindGroupLayoutEntry
        {
            Binding = 1, Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering }
        };
        bglEntries[2] = new BindGroupLayoutEntry
        {
            Binding = 2, Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform }
        };
        bglEntries[3] = new BindGroupLayoutEntry   // selection coverage mask (R8) for edge ants
        {
            Binding = 3, Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D
            }
        };
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 4, Entries = bglEntries };
        _bgl = api.DeviceCreateBindGroupLayout(gpu.Device, in bglDesc);

        // 1×1 R8 placeholder bound when no mask selection is active (binding must be satisfied)
        var dmDesc = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = 1, Height = 1, DepthOrArrayLayers = 1 },
            Format = TextureFormat.R8Unorm, MipLevelCount = 1, SampleCount = 1
        };
        _dummyMask = api.DeviceCreateTexture(gpu.Device, in dmDesc);
        _dummyMaskView = api.TextureCreateView(_dummyMask, null);

        var vpDesc = new BufferDescriptor { Size = 192, Usage = BufferUsage.Uniform | BufferUsage.CopyDst };
        _vpBuf = api.DeviceCreateBuffer(gpu.Device, in vpDesc);

        var bglLocal = _bgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var pipelineLayout = api.DeviceCreatePipelineLayout(gpu.Device, in plDesc);

        var module = gpu.CreateWgslModule("fullscreen_blit");
        var vsPtr = (byte*)SilkMarshal.StringToPtr("vs");
        var fsPtr = (byte*)SilkMarshal.StringToPtr("fs");
        var colorTarget = new ColorTargetState { Format = targetFormat, WriteMask = ColorWriteMask.All };
        var fragState = new FragmentState { Module = module, EntryPoint = fsPtr, TargetCount = 1, Targets = &colorTarget };
        var pipeDesc = new RenderPipelineDescriptor
        {
            Layout = pipelineLayout,
            Vertex = new VertexState { Module = module, EntryPoint = vsPtr, BufferCount = 0 },
            Fragment = &fragState,
            Primitive = new PrimitiveState
            {
                Topology = PrimitiveTopology.TriangleList,
                FrontFace = FrontFace.Ccw,
                CullMode = CullMode.None
            },
            Multisample = new MultisampleState { Count = 1, Mask = uint.MaxValue }
        };
        _pipeline = api.DeviceCreateRenderPipeline(gpu.Device, in pipeDesc);

        SilkMarshal.Free((nint)vsPtr);
        SilkMarshal.Free((nint)fsPtr);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);
    }

    /// <summary>Draw <paramref name="source"/> into <paramref name="target"/> with a viewport transform + optional overlays.</summary>
    public void Blit(TextureView* source, TextureView* target, in ViewportTransform vp, in BlitOverlay ov = default)
    {
        var api = _gpu.Api;

        // 48 floats / 192 bytes: viewport[0..7], rect[8..13], gizmo[16..25], brush[26..34],
        // maskOn[35], gradient[36..40]
        var u = stackalloc float[48];
        u[0] = vp.Ox; u[1] = vp.Oy; u[2] = vp.Scale > 0 ? 1f / vp.Scale : 0f; u[3] = 0;
        u[4] = vp.DocW; u[5] = vp.DocH; u[6] = 0; u[7] = 0;
        u[8] = ov.RectX; u[9] = ov.RectY; u[10] = ov.RectW; u[11] = ov.RectH; u[12] = ov.RectOn ? 1f : 0f;
        u[13] = ov.SelHandles ? 1f : 0f;
        if (ov.Corners is { Length: 8 })
            for (int i = 0; i < 8; i++) u[16 + i] = ov.Corners[i];
        u[24] = ov.GizmoOn ? 1f : 0f;
        u[25] = ov.RotateHandleDist;
        u[26] = ov.BrushOn ? 1f : 0f; u[27] = ov.BrushX; u[28] = ov.BrushY; u[29] = ov.BrushR;
        u[30] = ov.BrushColR; u[31] = ov.BrushColG; u[32] = ov.BrushColB;
        u[33] = ov.BrushErase ? 1f : 0f; u[34] = ov.BrushHardness;
        bool maskOn = ov.MaskOn && ov.MaskView is not null;
        u[35] = maskOn ? 1f : 0f;
        u[36] = ov.GradientOn ? 1f : 0f;
        u[37] = ov.GradX0; u[38] = ov.GradY0; u[39] = ov.GradX1; u[40] = ov.GradY1;
        u[41] = ov.CropOn ? 1f : 0f;
        api.QueueWriteBuffer(_gpu.Queue, _vpBuf, 0, u, 192);

        var maskView = maskOn ? ov.MaskView : _dummyMaskView;
        var bgEntries = stackalloc BindGroupEntry[4];
        bgEntries[0] = new BindGroupEntry { Binding = 0, TextureView = source };
        bgEntries[1] = new BindGroupEntry { Binding = 1, Sampler = _sampler };
        bgEntries[2] = new BindGroupEntry { Binding = 2, Buffer = _vpBuf, Size = 192 };
        bgEntries[3] = new BindGroupEntry { Binding = 3, TextureView = maskView };
        var bgDesc = new BindGroupDescriptor { Layout = _bgl, EntryCount = 4, Entries = bgEntries };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var color = new RenderPassColorAttachment
        {
            View = target,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0.08, G = 0.08, B = 0.09, A = 1.0 }
        };
        var rpDesc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color };

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var pass = api.CommandEncoderBeginRenderPass(encoder, in rpDesc);
        api.RenderPassEncoderSetPipeline(pass, _pipeline);
        api.RenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
        api.RenderPassEncoderEnd(pass);

        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);

        api.RenderPassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    public void Dispose()
    {
        var api = _gpu.Api;
        if (_pipeline is not null) api.RenderPipelineRelease(_pipeline);
        if (_bgl is not null) api.BindGroupLayoutRelease(_bgl);
        if (_sampler is not null) api.SamplerRelease(_sampler);
        if (_vpBuf is not null) api.BufferRelease(_vpBuf);
        if (_dummyMaskView is not null) api.TextureViewRelease(_dummyMaskView);
        if (_dummyMask is not null) api.TextureRelease(_dummyMask);
    }
}

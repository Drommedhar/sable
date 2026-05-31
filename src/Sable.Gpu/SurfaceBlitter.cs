using System;
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
    private Silk.NET.WebGPU.Buffer* _guidesBuf;   // guide line positions (storage)
    private Silk.NET.WebGPU.Buffer* _smartBuf;    // transient smart-guide alignment lines
    private Silk.NET.WebGPU.Buffer* _penBuf;      // pen-tool node geometry (storage)
    private const int GuidesFloats = 512;         // [countX, countY, _, _, Xs..., Ys...]
    private const int PenFloats = 512;            // [count, activeIdx, _, _, (ax,ay,inx,iny,outx,outy)×n]
    private const int VpFloats = 60;              // 240 bytes (16-byte aligned)
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

        var bglEntries = stackalloc BindGroupLayoutEntry[7];
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
        bglEntries[4] = new BindGroupLayoutEntry   // guides positions (storage): [countX, countY, _, _, Xs..., Ys...]
        {
            Binding = 4, Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
        };
        bglEntries[5] = new BindGroupLayoutEntry   // smart-guide (alignment) lines, same layout, magenta
        {
            Binding = 5, Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
        };
        bglEntries[6] = new BindGroupLayoutEntry   // pen-tool node geometry (storage)
        {
            Binding = 6, Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
        };
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 7, Entries = bglEntries };
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

        var vpDesc = new BufferDescriptor { Size = VpFloats * 4, Usage = BufferUsage.Uniform | BufferUsage.CopyDst };
        _vpBuf = api.DeviceCreateBuffer(gpu.Device, in vpDesc);

        var gDesc = new BufferDescriptor { Size = GuidesFloats * 4, Usage = BufferUsage.Storage | BufferUsage.CopyDst };
        _guidesBuf = api.DeviceCreateBuffer(gpu.Device, in gDesc);
        _smartBuf = api.DeviceCreateBuffer(gpu.Device, in gDesc);
        var penDesc = new BufferDescriptor { Size = PenFloats * 4, Usage = BufferUsage.Storage | BufferUsage.CopyDst };
        _penBuf = api.DeviceCreateBuffer(gpu.Device, in penDesc);

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

        // 52 floats / 208 bytes: viewport, rect, gizmo, brush, maskOn[35], gradient[36..40],
        // cropOn[41], shape[42..47], clone-source marker[48..50]
        var u = stackalloc float[VpFloats];
        bool hasPaste = ov.PasteR > 0 || ov.PasteG > 0 || ov.PasteB > 0;
        u[0] = vp.Ox; u[1] = vp.Oy; u[2] = vp.Scale > 0 ? 1f / vp.Scale : 0f; u[3] = hasPaste ? ov.PasteR : 0.16f;
        u[4] = vp.DocW; u[5] = vp.DocH; u[6] = hasPaste ? ov.PasteG : 0.16f; u[7] = hasPaste ? ov.PasteB : 0.17f;
        u[8] = ov.RectX; u[9] = ov.RectY; u[10] = ov.RectW; u[11] = ov.RectH; u[12] = ov.RectOn ? 1f : 0f;
        u[13] = ov.SelHandles ? 1f : 0f;
        u[14] = ov.GridOn ? 1f : 0f; u[15] = ov.GridSpacing;
        if (ov.Corners is { Length: 8 })
            for (int i = 0; i < 8; i++) u[16 + i] = ov.Corners[i];
        u[24] = ov.GizmoOn ? 1f : 0f;
        u[25] = ov.RotateHandleDist;
        u[26] = ov.BrushOn ? 1f : 0f; u[27] = ov.BrushX; u[28] = ov.BrushY; u[29] = ov.BrushR;
        u[30] = ov.BrushColR; u[31] = ov.BrushColG; u[32] = ov.BrushColB;
        u[33] = ov.BrushErase ? 1f : 0f; u[34] = ov.BrushHardness;
        bool maskOn = ov.MaskOn && ov.MaskView is not null;
        u[35] = maskOn ? (ov.QuickMask ? 2f : 1f) : 0f;   // 2 = rubylith fill, 1 = marching ants
        u[36] = ov.GradientOn ? 1f : 0f;
        u[37] = ov.GradX0; u[38] = ov.GradY0; u[39] = ov.GradX1; u[40] = ov.GradY1;
        u[41] = ov.CropOn ? 1f : 0f;
        u[42] = ov.ShapeOn ? 1f : 0f; u[43] = ov.ShapeKind;
        u[44] = ov.ShX0; u[45] = ov.ShY0; u[46] = ov.ShX1; u[47] = ov.ShY1;
        u[48] = ov.CloneSrcOn ? 1f : 0f; u[49] = ov.CloneSrcSx; u[50] = ov.CloneSrcSy; u[51] = ov.PixelGrid ? 1f : 0f;
        u[52] = ov.CaretOn ? 1f : 0f; u[53] = ov.CaretX; u[54] = ov.CaretY0; u[55] = ov.CaretY1;
        u[56] = ov.PenOn ? 1f : 0f;
        api.QueueWriteBuffer(_gpu.Queue, _vpBuf, 0, u, (uint)(VpFloats * 4));

        // pack guide positions: [countX, countY, _, _, Xs..., Ys...] (doc px)
        var gx = ov.GuidesX; var gy = ov.GuidesY;
        int cap = (GuidesFloats - 4) / 2;
        int nx = gx is null ? 0 : Math.Min(gx.Length, cap);
        int ny = gy is null ? 0 : Math.Min(gy.Length, cap);
        var gbuf = stackalloc float[GuidesFloats];
        gbuf[0] = nx; gbuf[1] = ny;
        for (int i = 0; i < nx; i++) gbuf[4 + i] = gx![i];
        for (int i = 0; i < ny; i++) gbuf[4 + cap + i] = gy![i];
        api.QueueWriteBuffer(_gpu.Queue, _guidesBuf, 0, gbuf, GuidesFloats * 4);

        // smart-guide (alignment) lines — same packing, drawn magenta
        var smx = ov.SmartX; var smy = ov.SmartY;
        int snx = smx is null ? 0 : Math.Min(smx.Length, cap);
        int sny = smy is null ? 0 : Math.Min(smy.Length, cap);
        var sbuf = stackalloc float[GuidesFloats];
        sbuf[0] = snx; sbuf[1] = sny;
        for (int i = 0; i < snx; i++) sbuf[4 + i] = smx![i];
        for (int i = 0; i < sny; i++) sbuf[4 + cap + i] = smy![i];
        api.QueueWriteBuffer(_gpu.Queue, _smartBuf, 0, sbuf, GuidesFloats * 4);

        // pen geometry: [nodeN, activeIdx, flatN, _, nodes(6×n)..., flatPts(2×m)...] surface px
        var pn = ov.PenNodes; var pf = ov.PenFlat;
        var pbuf = stackalloc float[PenFloats];
        int penN = pn is null ? 0 : pn.Length / 6;
        int nodeFloats = penN * 6;
        int flatRoom = PenFloats - 4 - nodeFloats;
        int flatN = pf is null ? 0 : Math.Min(pf.Length / 2, Math.Max(0, flatRoom / 2));
        // cap nodes so the whole thing fits
        if (4 + nodeFloats > PenFloats) { penN = (PenFloats - 4) / 6; nodeFloats = penN * 6; flatN = 0; }
        pbuf[0] = penN; pbuf[1] = ov.PenActive; pbuf[2] = flatN;
        for (int i = 0; i < nodeFloats; i++) pbuf[4 + i] = pn![i];
        int fb = 4 + nodeFloats;
        for (int i = 0; i < flatN * 2; i++) pbuf[fb + i] = pf![i];
        api.QueueWriteBuffer(_gpu.Queue, _penBuf, 0, pbuf, PenFloats * 4);

        var maskView = maskOn ? ov.MaskView : _dummyMaskView;
        var bgEntries = stackalloc BindGroupEntry[7];
        bgEntries[0] = new BindGroupEntry { Binding = 0, TextureView = source };
        bgEntries[1] = new BindGroupEntry { Binding = 1, Sampler = _sampler };
        bgEntries[2] = new BindGroupEntry { Binding = 2, Buffer = _vpBuf, Size = (uint)(VpFloats * 4) };
        bgEntries[3] = new BindGroupEntry { Binding = 3, TextureView = maskView };
        bgEntries[4] = new BindGroupEntry { Binding = 4, Buffer = _guidesBuf, Size = GuidesFloats * 4 };
        bgEntries[5] = new BindGroupEntry { Binding = 5, Buffer = _smartBuf, Size = GuidesFloats * 4 };
        bgEntries[6] = new BindGroupEntry { Binding = 6, Buffer = _penBuf, Size = PenFloats * 4 };
        var bgDesc = new BindGroupDescriptor { Layout = _bgl, EntryCount = 7, Entries = bgEntries };
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
        if (_guidesBuf is not null) api.BufferRelease(_guidesBuf);
        if (_smartBuf is not null) api.BufferRelease(_smartBuf);
        if (_penBuf is not null) api.BufferRelease(_penBuf);
        if (_dummyMaskView is not null) api.TextureViewRelease(_dummyMaskView);
        if (_dummyMask is not null) api.TextureRelease(_dummyMask);
    }
}

using Sable.Engine.Layers;
using Sable.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace Sable.Engine.Compositing;

/// <summary>
/// GPU compositor (PLAN §3): walks the layer tree bottom→top and blends each
/// visible layer onto an accumulator via composite.wgsl (blend mode + opacity),
/// ping-ponging two storage buffers. The final accumulator is copied to a texture
/// for presentation. Recomputes only when the document is dirty or resized.
///
/// M1 limitations (tracked follow-ups): full-resolution layer buffers (no tiling
/// yet); one submit per layer; assumes Width*4 is a multiple of 256 (copy row
/// alignment). Groups / adjustment / live-filter layers slot in here as the tree
/// walk grows.
/// </summary>
public sealed unsafe class GpuCompositor : IDisposable
{
    private readonly WgpuDevice _gpu;
    private readonly Dictionary<Layer, nint> _layerBuffers = new();
    private readonly Dictionary<Layer, nint> _maskBuffers = new();

    /// <summary>Live brush-preview dab composited into the stack after its target layer (null = none).</summary>
    public PreviewDab? Preview { get; set; }

    private ComputePipeline* _pipeline;
    private BindGroupLayout* _bgl;
    private ComputePipeline* _presentPipeline;
    private BindGroupLayout* _presentBgl;
    private ComputePipeline* _adjPipeline;
    private BindGroupLayout* _adjBgl;
    private ComputePipeline* _blurPipeline;
    private BindGroupLayout* _blurBgl;
    private ComputePipeline* _stampPipeline;
    private BindGroupLayout* _stampBgl;
    private Buffer* _stampParamsBuf;
    private Buffer* _dimsBuf;
    private Buffer* _paramsBuf;
    private Buffer* _adjParamsBuf;
    private Buffer* _blurParamsBuf;
    private Buffer* _filterTemp;
    private Buffer* _previewBuf;                                // active layer copy + preview dab
    private readonly List<(nint a, nint b)> _scratch = new();   // ping-pong pair per group depth
    private byte[] _zero = Array.Empty<byte>();
    private Buffer* _whiteMask;
    private Buffer* _readback;
    private Buffer* _lastResult;
    private Texture* _composite;
    private TextureView* _compositeView;

    private int _width, _height, _imgBytes;
    private bool _valid;

    public GpuCompositor(WgpuDevice gpu)
    {
        _gpu = gpu;
        BuildPipeline();
    }

    private void BuildPipeline()
    {
        var api = _gpu.Api;
        var bglEntries = stackalloc BindGroupLayoutEntry[6];
        bglEntries[0] = Entry(0, BufferBindingType.Uniform);
        bglEntries[1] = Entry(1, BufferBindingType.Uniform);
        bglEntries[2] = Entry(2, BufferBindingType.ReadOnlyStorage);
        bglEntries[3] = Entry(3, BufferBindingType.ReadOnlyStorage);
        bglEntries[4] = Entry(4, BufferBindingType.Storage);
        bglEntries[5] = Entry(5, BufferBindingType.ReadOnlyStorage);   // mask
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 6, Entries = bglEntries };
        _bgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);

        var bglLocal = _bgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var pipelineLayout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);

        var module = _gpu.CreateWgslModule("composite");
        var entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
        var cpDesc = new ComputePipelineDescriptor
        {
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
        };
        _pipeline = api.DeviceCreateComputePipeline(_gpu.Device, in cpDesc);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);

        _dimsBuf = NewBuffer(16, BufferUsage.Uniform | BufferUsage.CopyDst);
        _paramsBuf = NewBuffer(48, BufferUsage.Uniform | BufferUsage.CopyDst);
        _adjParamsBuf = NewBuffer(32, BufferUsage.Uniform | BufferUsage.CopyDst);
        _blurParamsBuf = NewBuffer(16, BufferUsage.Uniform | BufferUsage.CopyDst);

        _stampParamsBuf = NewBuffer(32, BufferUsage.Uniform | BufferUsage.CopyDst);

        BuildPresentPipeline();
        BuildAdjustPipeline();
        BuildBlurPipeline();
        BuildStampPipeline();
    }

    private void BuildStampPipeline()
    {
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[3];
        entries[0] = Entry(0, BufferBindingType.Uniform);     // dims
        entries[1] = Entry(1, BufferBindingType.Uniform);     // dab
        entries[2] = Entry(2, BufferBindingType.Storage);     // buffer (rw)
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 3, Entries = entries };
        _stampBgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);

        var bglLocal = _stampBgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var pipelineLayout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);
        var module = _gpu.CreateWgslModule("stamp");
        var entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
        var cpDesc = new ComputePipelineDescriptor
        {
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
        };
        _stampPipeline = api.DeviceCreateComputePipeline(_gpu.Device, in cpDesc);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);
    }

    private void BuildBlurPipeline()
    {
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[4];
        entries[0] = Entry(0, BufferBindingType.Uniform);          // dims
        entries[1] = Entry(1, BufferBindingType.Uniform);          // blur params
        entries[2] = Entry(2, BufferBindingType.ReadOnlyStorage);  // src
        entries[3] = Entry(3, BufferBindingType.Storage);          // out
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 4, Entries = entries };
        _blurBgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);

        var bglLocal = _blurBgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var pipelineLayout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);

        var module = _gpu.CreateWgslModule("blur");
        var entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
        var cpDesc = new ComputePipelineDescriptor
        {
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
        };
        _blurPipeline = api.DeviceCreateComputePipeline(_gpu.Device, in cpDesc);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);
    }

    private void BuildAdjustPipeline()
    {
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[5];
        entries[0] = Entry(0, BufferBindingType.Uniform);          // dims
        entries[1] = Entry(1, BufferBindingType.Uniform);          // adj params
        entries[2] = Entry(2, BufferBindingType.ReadOnlyStorage);  // src (backdrop)
        entries[3] = Entry(3, BufferBindingType.Storage);          // out
        entries[4] = Entry(4, BufferBindingType.ReadOnlyStorage);  // mask
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 5, Entries = entries };
        _adjBgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);

        var bglLocal = _adjBgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var pipelineLayout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);

        var module = _gpu.CreateWgslModule("adjust");
        var entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
        var cpDesc = new ComputePipelineDescriptor
        {
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
        };
        _adjPipeline = api.DeviceCreateComputePipeline(_gpu.Device, in cpDesc);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);
    }

    private void BuildPresentPipeline()
    {
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[3];
        entries[0] = Entry(0, BufferBindingType.Uniform);
        entries[1] = Entry(1, BufferBindingType.ReadOnlyStorage);
        entries[2] = new BindGroupLayoutEntry
        {
            Binding = 2, Visibility = ShaderStage.Compute,
            StorageTexture = new StorageTextureBindingLayout
            {
                Access = StorageTextureAccess.WriteOnly,
                Format = TextureFormat.Rgba8Unorm,
                ViewDimension = TextureViewDimension.Dimension2D
            }
        };
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 3, Entries = entries };
        _presentBgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);

        var bglLocal = _presentBgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var pipelineLayout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);

        var module = _gpu.CreateWgslModule("present_copy");
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        var cpDesc = new ComputePipelineDescriptor
        {
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
        };
        _presentPipeline = api.DeviceCreateComputePipeline(_gpu.Device, in cpDesc);
        SilkMarshal.Free((nint)entry);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);
    }

    private void EnsureSize(Document doc)
    {
        if (_valid && doc.Width == _width && doc.Height == _height) return;

        ReleaseSizeResources();
        _width = doc.Width;
        _height = doc.Height;
        _imgBytes = _width * _height * 4;

        _readback = NewBuffer(_imgBytes, BufferUsage.MapRead | BufferUsage.CopyDst);
        _filterTemp = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _previewBuf = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _zero = new byte[_imgBytes];

        // shared "fully revealing" white mask for layers without a mask
        _whiteMask = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst);
        var white = new byte[_imgBytes];
        Array.Fill(white, (byte)255);
        fixed (byte* pw = white) _gpu.Api.QueueWriteBuffer(_gpu.Queue, _whiteMask, 0, pw, (nuint)_imgBytes);

        var texDesc = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding | TextureUsage.StorageBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)_width, Height = (uint)_height, DepthOrArrayLayers = 1 },
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1
        };
        _composite = _gpu.Api.DeviceCreateTexture(_gpu.Device, in texDesc);
        _compositeView = _gpu.Api.TextureCreateView(_composite, null);

        var dims = stackalloc uint[4] { (uint)_width, (uint)_height, 0, 0 };
        _gpu.Api.QueueWriteBuffer(_gpu.Queue, _dimsBuf, 0, dims, 16);

        // invalidate cached layer + mask + scratch buffers (size changed)
        foreach (var p in _layerBuffers.Values) _gpu.Api.BufferRelease((Buffer*)p);
        _layerBuffers.Clear();
        foreach (var p in _maskBuffers.Values) _gpu.Api.BufferRelease((Buffer*)p);
        _maskBuffers.Clear();
        ReleaseScratch();
        _valid = true;
    }

    private void ScratchPair(int depth, out Buffer* a, out Buffer* b)
    {
        while (_scratch.Count <= depth)
        {
            var na = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
            var nb = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
            _scratch.Add(((nint)na, (nint)nb));
        }
        a = (Buffer*)_scratch[depth].a;
        b = (Buffer*)_scratch[depth].b;
    }

    private void ReleaseScratch()
    {
        foreach (var (a, b) in _scratch)
        {
            _gpu.Api.BufferRelease((Buffer*)a);
            _gpu.Api.BufferRelease((Buffer*)b);
        }
        _scratch.Clear();
    }

    private void ClearBuffer(Buffer* buf)
    {
        fixed (byte* pz = _zero) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, pz, (nuint)_imgBytes);
    }

    /// <summary>Composite the document and return the result texture view.</summary>
    public TextureView* Composite(Document doc)
    {
        EnsureSize(doc);

        var result = CompositeList(doc.Layers, 0);

        // copy result buffer -> composite storage texture (no row-alignment limit)
        _lastResult = result;
        RunPresentCopy(result);
        doc.ClearDirty();
        return _compositeView;
    }

    /// <summary>Composite a layer list (bottom→top) over transparent; returns the result buffer.</summary>
    private Buffer* CompositeList(List<Layer> layers, int depth)
    {
        var api = _gpu.Api;
        ScratchPair(depth, out Buffer* current, out Buffer* other);
        ClearBuffer(current);

        foreach (var layer in layers)
        {
            if (!layer.Visible || layer.Opacity <= 0f) continue;
            var maskBuf = GetMaskBuffer(layer);

            if (layer is PixelLayer px)
            {
                var srcBuf = GetLayerBuffer(px);
                // brush preview: stamp the dab into a copy of the layer, then composite normally
                // (so erase reveals layers below and paint respects the layer's blend/opacity)
                if (Preview is { } pv && ReferenceEquals(pv.Layer, layer))
                {
                    CopyBuffer(srcBuf, _previewBuf);
                    DispatchStamp(_previewBuf, pv);
                    srcBuf = _previewBuf;
                }
                BlendInto(ref current, ref other, srcBuf, layer, maskBuf);
            }
            else if (layer is GroupLayer grp)
            {
                var groupResult = CompositeList(grp.Children, depth + 1);   // isolated group
                BlendInto(ref current, ref other, groupResult, layer, maskBuf);
            }
            else if (layer is AdjustmentLayer adj)
            {
                var prm = stackalloc uint[8];
                prm[0] = (uint)adj.Kind;
                *(float*)(prm + 1) = adj.Opacity;
                var p = new Span<float>((float*)(prm + 2), 6);
                adj.PackParams(p);
                api.QueueWriteBuffer(_gpu.Queue, _adjParamsBuf, 0, prm, 32);
                DispatchAdjust(current, other, maskBuf);
                var t1 = current; current = other; other = t1;
            }
            else if (layer is FilterLayer flt)
            {
                WriteBlurParams(flt.Radius, 1f, 0f);
                DispatchBlur(current, _filterTemp);
                WriteBlurParams(flt.Radius, 0f, 1f);
                DispatchBlur(_filterTemp, other);
                var t2 = current; current = other; other = t2;
            }
        }
        return current;
    }

    // blend src (a pixel layer or a group's result) onto the accumulator
    private void BlendInto(ref Buffer* current, ref Buffer* other, Buffer* src, Layer layer, Buffer* maskBuf)
    {
        // layout (48B): mode(u32), opacity(f32), clip(f32), m00,m01,m10,m11,b0,b1 (f32), pad×3
        var prm = stackalloc float[12];
        ((uint*)prm)[0] = (uint)layer.BlendMode;
        prm[1] = layer.Opacity;
        prm[2] = layer.ClipToBelow ? 1f : 0f;
        var inv = AffineMath.DocToLayer(_width, _height,
            layer.OffsetX, layer.OffsetY, layer.ScaleX, layer.ScaleY, layer.Rotation);
        for (int i = 0; i < 6; i++) prm[3 + i] = inv[i];
        _gpu.Api.QueueWriteBuffer(_gpu.Queue, _paramsBuf, 0, prm, 48);
        DispatchBlend(current, src, other, maskBuf);
        var tmp = current; current = other; other = tmp;
    }

    /// <summary>Composite and read the flattened RGBA8 result back to the CPU (for export).</summary>
    public byte[] CompositeToBytes(Document doc)
    {
        Composite(doc);
        var api = _gpu.Api;

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        api.CommandEncoderCopyBufferToBuffer(encoder, _lastResult, 0, _readback, 0, (ulong)_imgBytes);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);

        bool mapped = false;
        var cb = PfnBufferMapCallback.From((status, _) =>
        {
            if (status != BufferMapAsyncStatus.Success)
                throw new InvalidOperationException($"wgpu: readback map failed: {status}");
            mapped = true;
        });
        api.BufferMapAsync(_readback, MapMode.Read, 0, (nuint)_imgBytes, cb, null);
        while (!mapped) _gpu.Poll(wait: true);

        var src = (byte*)api.BufferGetMappedRange(_readback, 0, (nuint)_imgBytes);
        var outBytes = new byte[_imgBytes];
        new ReadOnlySpan<byte>(src, _imgBytes).CopyTo(outBytes);
        api.BufferUnmap(_readback);
        return outBytes;
    }

    private void DispatchAdjust(Buffer* src, Buffer* outp, Buffer* mask)
    {
        var api = _gpu.Api;
        var bg = stackalloc BindGroupEntry[5];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _adjParamsBuf, Size = 32 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = src, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = outp, Size = (ulong)_imgBytes };
        bg[4] = new BindGroupEntry { Binding = 4, Buffer = mask, Size = (ulong)_imgBytes };
        var bgDesc = new BindGroupDescriptor { Layout = _adjBgl, EntryCount = 5, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, _adjPipeline);
        api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        api.ComputePassEncoderDispatchWorkgroups(pass, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        api.ComputePassEncoderEnd(pass);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);

        api.ComputePassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    private void CopyBuffer(Buffer* src, Buffer* dst)
    {
        var api = _gpu.Api;
        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        api.CommandEncoderCopyBufferToBuffer(encoder, src, 0, dst, 0, (ulong)_imgBytes);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
    }

    private void DispatchStamp(Buffer* buf, PreviewDab pv)
    {
        var api = _gpu.Api;
        var prm = stackalloc float[8]
        {
            pv.Cx, pv.Cy, pv.Radius, pv.Hardness,
            pv.R / 255f, pv.G / 255f, pv.B / 255f, pv.Erase ? 1f : 0f
        };
        api.QueueWriteBuffer(_gpu.Queue, _stampParamsBuf, 0, prm, 32);

        var bg = stackalloc BindGroupEntry[3];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _stampParamsBuf, Size = 32 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = buf, Size = (ulong)_imgBytes };
        var bgDesc = new BindGroupDescriptor { Layout = _stampBgl, EntryCount = 3, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, _stampPipeline);
        api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        api.ComputePassEncoderDispatchWorkgroups(pass, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        api.ComputePassEncoderEnd(pass);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        api.ComputePassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    private void WriteBlurParams(float radius, float dirX, float dirY)
    {
        var prm = stackalloc float[4] { radius, dirX, dirY, 0f };
        _gpu.Api.QueueWriteBuffer(_gpu.Queue, _blurParamsBuf, 0, prm, 16);
    }

    private void DispatchBlur(Buffer* src, Buffer* outp)
    {
        var api = _gpu.Api;
        var bg = stackalloc BindGroupEntry[4];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _blurParamsBuf, Size = 16 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = src, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = outp, Size = (ulong)_imgBytes };
        var bgDesc = new BindGroupDescriptor { Layout = _blurBgl, EntryCount = 4, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, _blurPipeline);
        api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        api.ComputePassEncoderDispatchWorkgroups(pass, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        api.ComputePassEncoderEnd(pass);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);

        api.ComputePassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    private void DispatchBlend(Buffer* dst, Buffer* src, Buffer* outp, Buffer* mask)
    {
        var api = _gpu.Api;
        var bg = stackalloc BindGroupEntry[6];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _paramsBuf, Size = 48 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = dst, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = src, Size = (ulong)_imgBytes };
        bg[4] = new BindGroupEntry { Binding = 4, Buffer = outp, Size = (ulong)_imgBytes };
        bg[5] = new BindGroupEntry { Binding = 5, Buffer = mask, Size = (ulong)_imgBytes };
        var bgDesc = new BindGroupDescriptor { Layout = _bgl, EntryCount = 6, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, _pipeline);
        api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        api.ComputePassEncoderDispatchWorkgroups(pass, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        api.ComputePassEncoderEnd(pass);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);

        api.ComputePassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    private void RunPresentCopy(Buffer* resultBuf)
    {
        var api = _gpu.Api;
        var bg = stackalloc BindGroupEntry[3];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = resultBuf, Size = (ulong)_imgBytes };
        bg[2] = new BindGroupEntry { Binding = 2, TextureView = _compositeView };
        var bgDesc = new BindGroupDescriptor { Layout = _presentBgl, EntryCount = 3, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, _presentPipeline);
        api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        api.ComputePassEncoderDispatchWorkgroups(pass, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        api.ComputePassEncoderEnd(pass);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        _gpu.Poll(wait: true);

        api.ComputePassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    private Buffer* GetLayerBuffer(PixelLayer px)
    {
        bool cached = _layerBuffers.TryGetValue(px, out var existing);
        if (cached && !px.Dirty) return (Buffer*)existing;

        Buffer* buf;
        if (cached)
        {
            buf = (Buffer*)existing;
            // partial upload: only the dirty tiles (row-by-row, since tiles are strided)
            if (px.DirtyTiles.Count > 0)
            {
                fixed (byte* p = px.Pixels)
                {
                    foreach (var (tx, ty) in px.DirtyTiles)
                    {
                        int tw = RasterTiles.TileWidth(px.Width, tx);
                        int th = RasterTiles.TileHeight(px.Height, ty);
                        for (int ry = 0; ry < th; ry++)
                        {
                            int off = ((ty * RasterTiles.TileSize + ry) * px.Width + tx * RasterTiles.TileSize) * 4;
                            _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, (ulong)off, p + off, (nuint)(tw * 4));
                        }
                    }
                }
            }
            else
            {
                // bulk/external change with no tile info → upload whole
                fixed (byte* p = px.Pixels) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)_imgBytes);
            }
        }
        else
        {
            buf = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
            _layerBuffers[px] = (nint)buf;
            fixed (byte* p = px.Pixels) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)_imgBytes);
        }

        px.DirtyTiles.Clear();
        px.Dirty = false;
        return buf;
    }

    private Buffer* GetMaskBuffer(Layer layer)
    {
        if (!layer.HasMask) return _whiteMask;

        _maskBuffers.TryGetValue(layer, out var existing);
        Buffer* buf;
        if (existing != 0 && !layer.MaskDirty) return (Buffer*)existing;
        if (existing != 0) buf = (Buffer*)existing;
        else
        {
            buf = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst);
            _maskBuffers[layer] = (nint)buf;
        }
        fixed (byte* p = layer.Mask!) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)_imgBytes);
        layer.MaskDirty = false;
        return buf;
    }

    private Buffer* NewBuffer(int size, BufferUsage usage)
    {
        var desc = new BufferDescriptor { Size = (ulong)size, Usage = usage };
        return _gpu.Api.DeviceCreateBuffer(_gpu.Device, in desc);
    }

    private static BindGroupLayoutEntry Entry(uint binding, BufferBindingType type) => new()
    {
        Binding = binding,
        Visibility = ShaderStage.Compute,
        Buffer = new BufferBindingLayout { Type = type }
    };

    private void ReleaseSizeResources()
    {
        var api = _gpu.Api;
        if (_compositeView is not null) { api.TextureViewRelease(_compositeView); _compositeView = null; }
        if (_composite is not null) { api.TextureRelease(_composite); _composite = null; }
        ReleaseScratch();
        if (_whiteMask is not null) { api.BufferRelease(_whiteMask); _whiteMask = null; }
        if (_readback is not null) { api.BufferRelease(_readback); _readback = null; }
        if (_filterTemp is not null) { api.BufferRelease(_filterTemp); _filterTemp = null; }
        if (_previewBuf is not null) { api.BufferRelease(_previewBuf); _previewBuf = null; }
        _lastResult = null;
    }

    public void Dispose()
    {
        var api = _gpu.Api;
        foreach (var p in _layerBuffers.Values) api.BufferRelease((Buffer*)p);
        _layerBuffers.Clear();
        foreach (var p in _maskBuffers.Values) api.BufferRelease((Buffer*)p);
        _maskBuffers.Clear();
        ReleaseSizeResources();
        if (_dimsBuf is not null) api.BufferRelease(_dimsBuf);
        if (_paramsBuf is not null) api.BufferRelease(_paramsBuf);
        if (_adjParamsBuf is not null) api.BufferRelease(_adjParamsBuf);
        if (_blurParamsBuf is not null) api.BufferRelease(_blurParamsBuf);
        if (_pipeline is not null) api.ComputePipelineRelease(_pipeline);
        if (_bgl is not null) api.BindGroupLayoutRelease(_bgl);
        if (_presentPipeline is not null) api.ComputePipelineRelease(_presentPipeline);
        if (_presentBgl is not null) api.BindGroupLayoutRelease(_presentBgl);
        if (_adjPipeline is not null) api.ComputePipelineRelease(_adjPipeline);
        if (_adjBgl is not null) api.BindGroupLayoutRelease(_adjBgl);
        if (_blurPipeline is not null) api.ComputePipelineRelease(_blurPipeline);
        if (_blurBgl is not null) api.BindGroupLayoutRelease(_blurBgl);
        if (_stampPipeline is not null) api.ComputePipelineRelease(_stampPipeline);
        if (_stampBgl is not null) api.BindGroupLayoutRelease(_stampBgl);
        if (_stampParamsBuf is not null) api.BufferRelease(_stampParamsBuf);
    }
}

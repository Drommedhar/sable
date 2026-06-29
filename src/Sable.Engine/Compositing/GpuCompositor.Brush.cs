using Sable.Engine.Layers;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace Sable.Engine.Compositing;

/// <summary>One brush dab, fully resolved on the CPU (jitter/pressure/spacing applied) and
/// stamped on the GPU by <c>brush.wgsl</c>. Coordinates are layer-buffer pixels.</summary>
public struct GpuDab
{
    public float Cx, Cy, R, Inner;
    public float CosA, SinA, Round, Sa;          // Sa = flow×jitter×alpha×pressure
    public float ColR, ColG, ColB, Strength;
    public uint Mode, Blend, Flags;
    public float CloneOffX, CloneOffY, Thx, Thy; // tip half-extents
    public int Bx, By, Bw, Bh;                   // dispatch bbox (buffer px)
}

/// <summary>Flags for <see cref="GpuDab.Flags"/> — keep in sync with brush.wgsl.</summary>
public static class GpuDabFlags
{
    public const uint Erase = 1, LockAlpha = 2, Pencil = 4, Tip = 8,
        Clone = 16, Heal = 32, ClipMask = 64, ClipRect = 128;
}

public sealed unsafe partial class GpuCompositor
{
    /// <summary>The pixel layer with a GPU stroke in progress. While set, the compositor
    /// samples this layer's live monolithic buffer (no CPU re-upload, no atlas).</summary>
    public PixelLayer? StrokeLayer { get; private set; }

    private BindGroupLayout* _brushBgl;
    private ComputePipeline* _brushStamp, _brushReduce, _brushPost, _brushClear;
    private Buffer* _brushDimsBuf, _brushParamsBuf, _brushStateBuf;
    private Buffer* _brushTipBuf, _brushClipBuf;
    private int _brushTipBytes, _brushClipBytes;
    private Buffer* _strokeSrc;            // stroke-start snapshot (clone/heal source)
    private int _strokeSrcBytes;
    private Buffer* _strokeReadback;
    private int _strokeReadbackBytes;
    private bool _brushTipBound, _brushClipBound;

    /// <summary>True when the GPU brush pipeline is usable (device alive).</summary>
    public bool BrushAvailable => _gpu is not null;

    private void EnsureBrushPipelines()
    {
        if (_brushStamp is not null) return;
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[7];
        entries[0] = Entry(0, BufferBindingType.Uniform);          // dims
        entries[1] = Entry(1, BufferBindingType.Uniform);          // params
        entries[2] = Entry(2, BufferBindingType.Storage);          // layer buffer (rw)
        entries[3] = Entry(3, BufferBindingType.ReadOnlyStorage);  // stroke-start snapshot
        entries[4] = Entry(4, BufferBindingType.ReadOnlyStorage);  // selection clip mask
        entries[5] = Entry(5, BufferBindingType.ReadOnlyStorage);  // sampled tip
        entries[6] = Entry(6, BufferBindingType.Storage);          // stroke state (carry + heal sums)
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 7, Entries = entries };
        _brushBgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);

        var bglLocal = _brushBgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var layout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);
        var module = _gpu.CreateWgslModule("brush");

        ComputePipeline* Make(string entryPoint)
        {
            var entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr(entryPoint);
            var desc = new ComputePipelineDescriptor
            {
                Layout = layout,
                Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
            };
            var pipe = api.DeviceCreateComputePipeline(_gpu.Device, in desc);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
            return pipe;
        }

        _brushStamp = Make("stamp");
        _brushReduce = Make("reduce");
        _brushPost = Make("post");
        _brushClear = Make("clearsums");
        api.PipelineLayoutRelease(layout);
        api.ShaderModuleRelease(module);

        _brushDimsBuf = NewBuffer(16, BufferUsage.Uniform | BufferUsage.CopyDst);
        _brushParamsBuf = NewBuffer(128, BufferUsage.Uniform | BufferUsage.CopyDst);
        _brushStateBuf = NewBuffer(48, BufferUsage.Storage | BufferUsage.CopyDst);
    }

    /// <summary>Start a GPU stroke on <paramref name="px"/>: upload the layer's current pixels,
    /// snapshot them (clone/heal source), upload the sampled tip + selection mask, reset state.</summary>
    public void BeginBrushStroke(PixelLayer px, byte[]? tip, int tipW, int tipH, byte[]? clipMask, int docW, int docH)
    {
        EnsureBrushPipelines();
        var api = _gpu.Api;

        var lb = GetLayerBuffer(px);                  // uploads (ExpandToCover marked it dirty)
        int bytes = px.Width * px.Height * 16;        // f32 rgba
        if (_strokeSrcBytes < bytes)
        {
            if (_strokeSrc is not null) api.BufferRelease(_strokeSrc);
            _strokeSrc = NewBuffer(bytes, BufferUsage.Storage | BufferUsage.CopyDst);
            _strokeSrcBytes = bytes;
        }
        CopyBuffer(lb, _strokeSrc, bytes);

        _brushTipBound = tip is not null && tipW > 0 && tipH > 0;
        if (_brushTipBound)
        {
            int tb = tipW * tipH * 4;
            if (_brushTipBytes < tb)
            {
                if (_brushTipBuf is not null) api.BufferRelease(_brushTipBuf);
                _brushTipBuf = NewBuffer(tb, BufferUsage.Storage | BufferUsage.CopyDst);
                _brushTipBytes = tb;
            }
            var tf = new float[tipW * tipH];
            for (int i = 0; i < tf.Length; i++) tf[i] = tip![i] / 255f;
            fixed (float* fp = tf) api.QueueWriteBuffer(_gpu.Queue, _brushTipBuf, 0, fp, (nuint)tb);
        }

        _brushClipBound = clipMask is not null;
        if (_brushClipBound)
        {
            int cb = docW * docH * 4;
            if (_brushClipBytes < cb)
            {
                if (_brushClipBuf is not null) api.BufferRelease(_brushClipBuf);
                _brushClipBuf = NewBuffer(cb, BufferUsage.Storage | BufferUsage.CopyDst);
                _brushClipBytes = cb;
            }
            var cf = new float[docW * docH];
            for (int i = 0; i < cf.Length; i++) cf[i] = clipMask![i] / 255f;
            fixed (float* fp = cf) api.QueueWriteBuffer(_gpu.Queue, _brushClipBuf, 0, fp, (nuint)cb);
        }

        var zero = stackalloc byte[48];
        api.QueueWriteBuffer(_gpu.Queue, _brushStateBuf, 0, zero, 48);

        StrokeLayer = px;
    }

    /// <summary>Stamp one dab into the live stroke buffer (heal adds a clear+reduce pre-pass,
    /// smudge a carry update after).</summary>
    public void StampBrushDab(in GpuDab d)
    {
        if (StrokeLayer is not { } px) return;
        var api = _gpu.Api;
        var lb = GetLayerBuffer(px);                  // clean during the stroke → cached, no upload
        ulong bytes = (ulong)(px.Width * px.Height * 16);

        var dims = stackalloc uint[4] { (uint)px.Width, (uint)px.Height, (uint)d.Bx, (uint)d.By };
        api.QueueWriteBuffer(_gpu.Queue, _brushDimsBuf, 0, dims, 16);

        var prm = stackalloc uint[32];
        var f = (float*)prm;
        f[0] = d.Cx; f[1] = d.Cy; f[2] = d.R; f[3] = d.Inner;
        f[4] = d.CosA; f[5] = d.SinA; f[6] = d.Round; f[7] = d.Sa;
        f[8] = d.ColR; f[9] = d.ColG; f[10] = d.ColB; f[11] = d.Strength;
        prm[12] = d.Mode; prm[13] = d.Blend; prm[14] = d.Flags; prm[15] = (uint)_dabTipW;
        prm[16] = (uint)_dabTipH; prm[17] = (uint)px.OffsetX; prm[18] = (uint)px.OffsetY; prm[19] = (uint)_dabClipW;
        f[20] = _dabClipX0; f[21] = _dabClipY0; f[22] = _dabClipX1; f[23] = _dabClipY1;
        f[24] = d.CloneOffX; f[25] = d.CloneOffY; f[26] = d.Thx; f[27] = d.Thy;
        prm[28] = (uint)_dabDocH; f[29] = 0; f[30] = 0; f[31] = 0;
        api.QueueWriteBuffer(_gpu.Queue, _brushParamsBuf, 0, prm, 128);

        var bg = stackalloc BindGroupEntry[7];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _brushDimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _brushParamsBuf, Size = 128 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = lb, Size = bytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = _strokeSrc, Size = bytes };
        bg[4] = new BindGroupEntry { Binding = 4, Buffer = _brushClipBound ? _brushClipBuf : _dummyStore, Size = _brushClipBound ? (ulong)_brushClipBytes : 16 };
        bg[5] = new BindGroupEntry { Binding = 5, Buffer = _brushTipBound ? _brushTipBuf : _dummyStore, Size = _brushTipBound ? (ulong)_brushTipBytes : 16 };
        bg[6] = new BindGroupEntry { Binding = 6, Buffer = _brushStateBuf, Size = 48 };
        var bgDesc = new BindGroupDescriptor { Layout = _brushBgl, EntryCount = 7, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);

        uint gx = (uint)((d.Bw + 15) / 16), gy = (uint)((d.Bh + 15) / 16);
        bool heal = (d.Flags & GpuDabFlags.Heal) != 0;
        if (heal)
        {
            api.ComputePassEncoderSetPipeline(pass, _brushClear);
            api.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
            api.ComputePassEncoderSetPipeline(pass, _brushReduce);
            api.ComputePassEncoderDispatchWorkgroups(pass, gx, gy, 1);
        }
        api.ComputePassEncoderSetPipeline(pass, _brushStamp);
        api.ComputePassEncoderDispatchWorkgroups(pass, gx, gy, 1);
        if (d.Mode == 6)   // smudge: update the carried colour after the dab
        {
            api.ComputePassEncoderSetPipeline(pass, _brushPost);
            api.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
        }
        api.ComputePassEncoderEnd(pass);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        api.ComputePassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    // per-stroke statics the dab params need (set by ConfigureBrushClip)
    private int _dabTipW, _dabTipH, _dabClipW, _dabDocH;
    private float _dabClipX0, _dabClipY0, _dabClipX1, _dabClipY1;

    /// <summary>Per-stroke clip/tip statics packed into every dab's params.</summary>
    public void ConfigureBrushClip(int tipW, int tipH, int clipW, int docH,
        float clipX0, float clipY0, float clipX1, float clipY1)
    {
        _dabTipW = tipW; _dabTipH = tipH; _dabClipW = clipW; _dabDocH = docH;
        _dabClipX0 = clipX0; _dabClipY0 = clipY0; _dabClipX1 = clipX1; _dabClipY1 = clipY1;
    }

    /// <summary>Finish the GPU stroke: read the f32 layer buffer back into <c>px.Pixels</c>
    /// (RGBA32F) so the CPU copy is authoritative again. Caller marks tiles/undo.</summary>
    public void EndBrushStroke()
    {
        if (StrokeLayer is not { } px) return;
        var api = _gpu.Api;
        var lb = GetLayerBuffer(px);
        int bytes = px.Width * px.Height * 16;
        if (_strokeReadbackBytes < bytes)
        {
            if (_strokeReadback is not null) api.BufferRelease(_strokeReadback);
            _strokeReadback = NewBuffer(bytes, BufferUsage.MapRead | BufferUsage.CopyDst);
            _strokeReadbackBytes = bytes;
        }

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        api.CommandEncoderCopyBufferToBuffer(encoder, lb, 0, _strokeReadback, 0, (ulong)bytes);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);

        bool mapped = false;
        var cb = PfnBufferMapCallback.From((status, _) =>
        {
            if (status != BufferMapAsyncStatus.Success)
                throw new InvalidOperationException($"wgpu: stroke readback map failed: {status}");
            mapped = true;
        });
        api.BufferMapAsync(_strokeReadback, MapMode.Read, 0, (nuint)bytes, cb, null);
        while (!mapped) _gpu.Poll(wait: true);

        var srcF = (float*)api.BufferGetMappedRange(_strokeReadback, 0, (nuint)bytes);
        var dst = px.Pixels;   // GPU buffer is already RGBA32F working units → straight copy
        new ReadOnlySpan<float>(srcF, dst.Length).CopyTo(dst);
        api.BufferUnmap(_strokeReadback);

        StrokeLayer = null;
    }

    /// <summary>Abort a GPU stroke without readback (e.g. tab/document switch).</summary>
    public void CancelBrushStroke() => StrokeLayer = null;
}

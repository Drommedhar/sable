using Sable.Core;
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
    private readonly Dictionary<Layer, int> _layerBufferBytes = new();   // allocated size per layer buffer (for grow/realloc detection)
    private readonly Dictionary<Layer, nint> _maskBuffers = new();
    private readonly Dictionary<Layer, int> _maskBufferBytes = new();

    // --- GPU-tiled layer storage (PLAN §3/§17.3): a shared 256×256 tile atlas with LRU residency.
    // Pixel layers sample through a per-layer tile table → atlas slot instead of a monolithic buffer,
    // so only resident (recently-touched, non-empty) tiles consume VRAM. Layers whose live tile count
    // exceeds the atlas fall back to the monolithic GetLayerBuffer path.
    private const int TileSz = 256;
    private const int TileWords = TileSz * TileSz;            // 65536 u32 per slot
    private const int TileBytes = TileWords * 4;             // 256 KiB per slot
    private Buffer* _atlasBuf;
    private ulong _atlasBytes;
    private int _atlasSlots;
    private TileResidency? _residency;
    private Buffer* _tileTableBuf;                            // per-layer table, reuploaded each composite
    private int _tileTableWords;                             // _tileTableBuf capacity (u32)
    private uint[] _tableScratch = System.Array.Empty<uint>();
    private Buffer* _dummyStore;                              // 4-byte filler for unused storage bindings
    private readonly Dictionary<(Layer, int, int), bool> _emptyTiles = new();   // tile transparency cache
    private readonly Dictionary<Layer, (int w, int h)> _layerTileDims = new();   // detect a layer resize → reset tiles

    // --- composite-cache (PLAN §7 hot-path): cached accumulator below the active (edited) layer ---
    /// <summary>The layer currently being edited; the backdrop below it is reused while painting.</summary>
    public Layer? CacheHintLayer { get; set; }
    private Buffer* _backdropCache;        // doc-sized snapshot of the accumulator below the active layer
    private bool _cacheValid;
    private Layer? _cacheActive;           // the active layer the snapshot was built for
    private Layer[]? _cachePrefix;         // the layers below active, to detect a structural change

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
    private Buffer* _curveLutBuf;
    private readonly float[] _lutScratch = new float[AdjustmentLayer.CurveChannels * AdjustmentLayer.LutSize];
    private Buffer* _blurParamsBuf;
    private Buffer* _filterTemp;
    private Buffer* _previewBuf;                                // active layer copy + preview dab
    private int _previewBytes;                                  // current _previewBuf allocation (grows for oversized layers)
    private ComputePipeline* _fxPipeline;
    private BindGroupLayout* _fxBgl;
    private Buffer* _fxParamsBuf;
    private ComputePipeline* _dirPipeline;      // motion/zoom blur (reuses _blurBgl)
    private ComputePipeline* _convPipeline;     // sharpen (reuses _blurBgl)
    private ComputePipeline* _noisePipeline;    // add-noise/denoise (reuses _blurBgl)
    private ComputePipeline* _combinePipeline;  // unsharp/high-pass/clarity (2 inputs)
    private BindGroupLayout* _combineBgl;
    private Buffer* _filterParamsBuf;           // 32B shared filter params
    private Buffer* _fxLdoc;     // layer rendered in doc space (effect source)
    private Buffer* _fxTint;     // effect sprite (tint / stroke / blur ping)
    private Buffer* _fxBlur;     // blur pong
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
        var bglEntries = stackalloc BindGroupLayoutEntry[8];
        bglEntries[0] = Entry(0, BufferBindingType.Uniform);
        bglEntries[1] = Entry(1, BufferBindingType.Uniform);
        bglEntries[2] = Entry(2, BufferBindingType.ReadOnlyStorage);
        bglEntries[3] = Entry(3, BufferBindingType.ReadOnlyStorage);
        bglEntries[4] = Entry(4, BufferBindingType.Storage);
        bglEntries[5] = Entry(5, BufferBindingType.ReadOnlyStorage);   // mask
        bglEntries[6] = Entry(6, BufferBindingType.ReadOnlyStorage);   // tile table (atlas mode)
        bglEntries[7] = Entry(7, BufferBindingType.ReadOnlyStorage);   // tile atlas (atlas mode)
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 8, Entries = bglEntries };
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
        _paramsBuf = NewBuffer(64, BufferUsage.Uniform | BufferUsage.CopyDst);
        _adjParamsBuf = NewBuffer(64, BufferUsage.Uniform | BufferUsage.CopyDst);
        _curveLutBuf = NewBuffer(4 * 256 * 4, BufferUsage.Storage | BufferUsage.CopyDst); // 4ch×256×f32
        _blurParamsBuf = NewBuffer(16, BufferUsage.Uniform | BufferUsage.CopyDst);

        _stampParamsBuf = NewBuffer(48, BufferUsage.Uniform | BufferUsage.CopyDst);
        _fxParamsBuf = NewBuffer(48, BufferUsage.Uniform | BufferUsage.CopyDst);
        _filterParamsBuf = NewBuffer(32, BufferUsage.Uniform | BufferUsage.CopyDst);

        // tile atlas: cap to the device's storage-buffer binding limit (and a sane budget).
        const ulong budget = 768UL * 1024 * 1024;
        ulong cap = System.Math.Min(System.Math.Min(_gpu.MaxStorageBinding, _gpu.MaxBufferSize), budget);
        _atlasSlots = System.Math.Max(8, (int)(cap / TileBytes));
        _atlasBytes = (ulong)_atlasSlots * TileBytes;
        _atlasBuf = NewBuffer((int)_atlasBytes, BufferUsage.Storage | BufferUsage.CopyDst);
        _residency = new TileResidency(_atlasSlots);
        _dummyStore = NewBuffer(4, BufferUsage.Storage | BufferUsage.CopyDst);
        _tileTableWords = 4096;   // grows on demand
        _tileTableBuf = NewBuffer(_tileTableWords * 4, BufferUsage.Storage | BufferUsage.CopyDst);
        _tableScratch = new uint[_tileTableWords];

        BuildPresentPipeline();
        BuildAdjustPipeline();
        BuildBlurPipeline();
        BuildStampPipeline();
        BuildFxPipeline();
        BuildFilterPipelines();
    }

    // motion/zoom/sharpen/noise reuse the 4-binding blur layout; combine needs 5 bindings.
    private void BuildFilterPipelines()
    {
        var api = _gpu.Api;
        _dirPipeline = MakeComputePipeline("filter_dir", _blurBgl);
        _convPipeline = MakeComputePipeline("filter_conv", _blurBgl);
        _noisePipeline = MakeComputePipeline("filter_noise", _blurBgl);

        var entries = stackalloc BindGroupLayoutEntry[5];
        entries[0] = Entry(0, BufferBindingType.Uniform);
        entries[1] = Entry(1, BufferBindingType.Uniform);
        entries[2] = Entry(2, BufferBindingType.ReadOnlyStorage);   // src
        entries[3] = Entry(3, BufferBindingType.ReadOnlyStorage);   // blurred
        entries[4] = Entry(4, BufferBindingType.Storage);           // out
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 5, Entries = entries };
        _combineBgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);
        _combinePipeline = MakeComputePipeline("filter_combine", _combineBgl);
    }

    // build a compute pipeline from an embedded WGSL module + an existing bind-group layout
    private ComputePipeline* MakeComputePipeline(string shader, BindGroupLayout* bgl)
    {
        var api = _gpu.Api;
        var local = bgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &local };
        var pipelineLayout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);
        var module = _gpu.CreateWgslModule(shader);
        var entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
        var cpDesc = new ComputePipelineDescriptor
        {
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
        };
        var pipe = api.DeviceCreateComputePipeline(_gpu.Device, in cpDesc);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);
        return pipe;
    }

    private void BuildStampPipeline()
    {
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[4];
        entries[0] = Entry(0, BufferBindingType.Uniform);          // dims
        entries[1] = Entry(1, BufferBindingType.Uniform);          // dab
        entries[2] = Entry(2, BufferBindingType.Storage);          // buffer (rw)
        entries[3] = Entry(3, BufferBindingType.ReadOnlyStorage);  // clone source
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 4, Entries = entries };
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

    private void BuildFxPipeline()
    {
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[4];
        entries[0] = Entry(0, BufferBindingType.Uniform);          // dims
        entries[1] = Entry(1, BufferBindingType.Uniform);          // fx params
        entries[2] = Entry(2, BufferBindingType.ReadOnlyStorage);  // src (layer in doc space)
        entries[3] = Entry(3, BufferBindingType.Storage);          // out (effect sprite)
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 4, Entries = entries };
        _fxBgl = api.DeviceCreateBindGroupLayout(_gpu.Device, in bglDesc);

        var bglLocal = _fxBgl;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
        var pipelineLayout = api.DeviceCreatePipelineLayout(_gpu.Device, in plDesc);

        var module = _gpu.CreateWgslModule("fx");
        var entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
        var cpDesc = new ComputePipelineDescriptor
        {
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entry }
        };
        _fxPipeline = api.DeviceCreateComputePipeline(_gpu.Device, in cpDesc);
        Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        api.PipelineLayoutRelease(pipelineLayout);
        api.ShaderModuleRelease(module);
    }

    private void BuildAdjustPipeline()
    {
        var api = _gpu.Api;
        var entries = stackalloc BindGroupLayoutEntry[6];
        entries[0] = Entry(0, BufferBindingType.Uniform);          // dims
        entries[1] = Entry(1, BufferBindingType.Uniform);          // adj params
        entries[2] = Entry(2, BufferBindingType.ReadOnlyStorage);  // src (backdrop)
        entries[3] = Entry(3, BufferBindingType.Storage);          // out
        entries[4] = Entry(4, BufferBindingType.ReadOnlyStorage);  // mask
        entries[5] = Entry(5, BufferBindingType.ReadOnlyStorage);  // curve LUT
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 6, Entries = entries };
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
        _previewBytes = _imgBytes;
        _fxLdoc = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _fxTint = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _fxBlur = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _backdropCache = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _cacheValid = false;   // size changed → old snapshot invalid
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
        _layerBufferBytes.Clear();
        foreach (var p in _maskBuffers.Values) _gpu.Api.BufferRelease((Buffer*)p);
        _maskBuffers.Clear();
        _maskBufferBytes.Clear();
        // tiled storage: tile coords are doc-size independent, but a same-doc canvas resize is rare
        // and re-uploading is cheap, so reset residency to stay simple + correct.
        _residency?.Clear();
        _emptyTiles.Clear();
        _layerTileDims.Clear();
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

    /// <summary>
    /// Release all cached per-layer GPU buffers (layer pixels + masks). Call when the
    /// document is swapped (open/new tab) — the old doc's layers are gone, so their
    /// cached buffers would otherwise leak. New buffers rebuild lazily next composite.
    /// </summary>
    public void ReleaseLayerCaches()
    {
        foreach (var p in _layerBuffers.Values) _gpu.Api.BufferRelease((Buffer*)p);
        _layerBuffers.Clear();
        _layerBufferBytes.Clear();
        foreach (var p in _maskBuffers.Values) _gpu.Api.BufferRelease((Buffer*)p);
        _maskBuffers.Clear();
        _maskBufferBytes.Clear();
        // tiled storage: drop residency for the old doc's layers (atlas buffer itself is reused)
        _residency?.Clear();
        _emptyTiles.Clear();
        _layerTileDims.Clear();
        // composite-cache belongs to the old doc's layers
        _cacheValid = false;
        _cacheActive = null;
        _cachePrefix = null;
    }

    /// <summary>Composite the document and return the result texture view.</summary>
    public TextureView* Composite(Document doc)
    {
        EnsureSize(doc);

        var result = CompositeRoot(doc);

        // copy result buffer -> composite storage texture (no row-alignment limit)
        _lastResult = result;
        RunPresentCopy(result);
        doc.ClearDirty();
        return _compositeView;
    }

    /// <summary>
    /// Composite the root layer list, reusing a cached backdrop below the active (edited) layer when
    /// nothing under it changed (PLAN §7 hot-path). On a brush stroke only the active layer + the
    /// layers above it are re-blended; everything below is the unchanged <see cref="_backdropCache"/>.
    /// </summary>
    private Buffer* CompositeRoot(Document doc)
    {
        var layers = doc.Layers;
        int ai = CacheHintLayer is null ? -1 : layers.IndexOf(CacheHintLayer);
        bool canCache = ai >= 1 && _backdropCache is not null;

        // fast path: the prefix (layers below active) is identical + clean → reuse the snapshot
        if (canCache && _cacheValid && ReferenceEquals(_cacheActive, CacheHintLayer)
            && PrefixMatches(layers, ai) && !PrefixDirty(layers, ai))
        {
            ScratchPair(0, out Buffer* cur, out Buffer* oth);
            CopyBuffer(_backdropCache, cur, _imgBytes);
            for (int i = ai; i < layers.Count; i++) BlendOneLayer(layers[i], ref cur, ref oth, 0);
            return cur;
        }

        // full walk; snapshot the backdrop the moment we reach the active layer
        ScratchPair(0, out Buffer* current, out Buffer* other);
        ClearBuffer(current);
        for (int i = 0; i < layers.Count; i++)
        {
            if (canCache && i == ai)
            {
                CopyBuffer(current, _backdropCache, _imgBytes);
                _cacheActive = CacheHintLayer;
                _cachePrefix = layers.GetRange(0, ai).ToArray();
                _cacheValid = true;
            }
            BlendOneLayer(layers[i], ref current, ref other, 0);
        }
        if (!canCache) _cacheValid = false;
        return current;
    }

    private bool PrefixMatches(List<Layer> layers, int ai)
    {
        if (_cachePrefix is null || _cachePrefix.Length != ai) return false;
        for (int i = 0; i < ai; i++) if (!ReferenceEquals(_cachePrefix[i], layers[i])) return false;
        return true;
    }

    // any layer below the active one changed (param/mask/structure of a group) → cache stale
    private static bool PrefixDirty(List<Layer> layers, int ai)
    {
        for (int i = 0; i < ai; i++) if (SubtreeDirty(layers[i])) return true;
        return false;
    }

    private static bool SubtreeDirty(Layer l)
    {
        if (l.Dirty || l.MaskDirty) return true;
        if (l is GroupLayer g) foreach (var c in g.Children) if (SubtreeDirty(c)) return true;
        return false;
    }

    /// <summary>Composite a layer list (bottom→top) over transparent; returns the result buffer.</summary>
    private Buffer* CompositeList(List<Layer> layers, int depth)
    {
        ScratchPair(depth, out Buffer* current, out Buffer* other);
        ClearBuffer(current);
        foreach (var layer in layers) BlendOneLayer(layer, ref current, ref other, depth);
        return current;
    }

    /// <summary>Blend one layer onto the accumulator (ping-pongs current/other). No-op if hidden.</summary>
    private void BlendOneLayer(Layer layer, ref Buffer* current, ref Buffer* other, int depth)
    {
        if (!layer.Visible || layer.Opacity <= 0f) return;
        var api = _gpu.Api;
        var maskBuf = GetMaskBuffer(layer);

        {
            if (layer is PixelLayer px)
            {
                bool previewing = Preview is { } pv0 && ReferenceEquals(pv0.Layer, layer);
                Buffer* srcBuf;
                Buffer* tileTable = null;
                if (previewing)
                {
                    // brush preview needs a contiguous buffer to stamp the dab into a copy, then
                    // composite normally (so erase reveals below + paint respects blend/opacity).
                    // The actively-painted layer therefore stays on the monolithic path during hover.
                    var pv = Preview!.Value;
                    srcBuf = GetLayerBuffer(px);
                    int layerBytes = px.Width * px.Height * 4;
                    EnsurePreviewBuffer(layerBytes);
                    CopyBuffer(srcBuf, _previewBuf, layerBytes);
                    DispatchStamp(_previewBuf, srcBuf, pv, px.Width, px.Height, px.OffsetX, px.OffsetY);
                    srcBuf = _previewBuf;
                }
                else
                {
                    // tiled atlas path (PLAN §3): sample resident tiles via a per-layer table; monster
                    // layers (more tiles than the atlas holds) fall back to the monolithic buffer.
                    tileTable = GetLayerTiles(px);
                    srcBuf = tileTable is not null ? _dummyStore : GetLayerBuffer(px);
                }
                BlendContentWithFx(ref current, ref other, srcBuf, layer, maskBuf, px.Width, px.Height, tileTable);
            }
            else if (layer is ShapeLayer sh)
            {
                BlendContentWithFx(ref current, ref other, GetShapeBuffer(sh), layer, maskBuf, _width, _height);
            }
            else if (layer is TextLayer txt)
            {
                BlendContentWithFx(ref current, ref other, GetTextBuffer(txt), layer, maskBuf, _width, _height);
            }
            else if (layer is PathLayer pth)
            {
                BlendContentWithFx(ref current, ref other, GetPathBuffer(pth), layer, maskBuf, _width, _height);
            }
            else if (layer is GroupLayer grp)
            {
                var groupResult = CompositeList(grp.Children, depth + 1);   // isolated group
                BlendContentWithFx(ref current, ref other, groupResult, layer, maskBuf, _width, _height);
            }
            else if (layer is AdjustmentLayer adj)
            {
                var prm = stackalloc uint[16];   // 64B: kind + opacity + p0..p11 + 2 pad
                prm[0] = (uint)adj.Kind;
                *(float*)(prm + 1) = adj.Opacity;
                var p = new Span<float>((float*)(prm + 2), 12);
                adj.PackParams(p);
                api.QueueWriteBuffer(_gpu.Queue, _adjParamsBuf, 0, prm, 64);
                if (adj.Kind == AdjustmentKind.Curves)
                {
                    adj.BuildLut(_lutScratch);
                    fixed (float* lp = _lutScratch)
                        api.QueueWriteBuffer(_gpu.Queue, _curveLutBuf, 0, lp, (nuint)(_lutScratch.Length * 4));
                }
                // adjustment layers are document-sized → the doc-sized white mask is a valid no-mask fallback
                DispatchAdjust(current, other, maskBuf is not null ? maskBuf : _whiteMask);
                var t1 = current; current = other; other = t1;
            }
            else if (layer is FilterLayer flt)
            {
                RenderFilter(flt, current, _fxTint);   // filtered backdrop → _fxTint
                // blend the filtered result back over the backdrop with the layer's opacity + mask
                BlendBufferInto(ref current, ref other, _fxTint, BlendMode.Normal, layer.Opacity, 0f, 0f, maskBuf);
            }
        }
    }

    // write the 64B blend params: mode(u32), opacity, clip, inv-affine(6), fill, hasMask,
    // perspective row h6,h7,h8 (affine → 0,0,1), 2 pad
    private void WriteBlendParams(uint mode, float opacity, float clip, ReadOnlySpan<float> inv, float fill, bool hasMask,
        ReadOnlySpan<float> perspRow = default, uint srcMode = 0)
    {
        var prm = stackalloc float[16];
        ((uint*)prm)[0] = mode;
        prm[1] = opacity;
        prm[2] = clip;
        for (int i = 0; i < 6; i++) prm[3 + i] = inv[i];
        prm[9] = fill;
        prm[10] = hasMask ? 1f : 0f;
        prm[11] = perspRow.Length == 3 ? perspRow[0] : 0f;   // h6
        prm[12] = perspRow.Length == 3 ? perspRow[1] : 0f;   // h7
        prm[13] = perspRow.Length == 3 ? perspRow[2] : 1f;   // h8
        ((uint*)prm)[14] = srcMode;                          // 0 = contiguous src, 1 = tiled atlas
        _gpu.Api.QueueWriteBuffer(_gpu.Queue, _paramsBuf, 0, prm, 64);
    }

    // blend src (a pixel layer or a group's result) onto the accumulator.
    // srcW/srcH = the source buffer's own size; the transform pivots about the source's centre,
    // and OffsetX/Y places the source's top-left in document space.
    // inverse map (doc→layer) for a layer: affine (6 floats + identity persp row) or a full
    // homography when the layer's perspective corners are active.
    private static (float[] inv, float[] persp) LayerInverse(Layer layer, int srcW, int srcH)
    {
        if (layer.Perspective && layer.PerspCorners is { Length: 8 } pc)
            return Homography.DocToLayerQuad(srcW, srcH, pc);
        var inv = AffineMath.DocToLayer(srcW, srcH,
            layer.OffsetX, layer.OffsetY, layer.ScaleX, layer.ScaleY, layer.Rotation, layer.ShearX, layer.ShearY);
        return (inv, new[] { 0f, 0f, 1f });
    }

    private void BlendInto(ref Buffer* current, ref Buffer* other, Buffer* src, Layer layer, Buffer* maskBuf, int srcW, int srcH,
        Buffer* tileTable = null)
    {
        var (inv, persp) = LayerInverse(layer, srcW, srcH);
        WriteBlendParams((uint)layer.BlendMode, layer.Opacity, layer.ClipToBelow ? 1f : 0f, inv, layer.FillOpacity,
            maskBuf is not null, persp, tileTable is not null ? 1u : 0u);
        DispatchBlend(current, src, other, maskBuf, srcW, srcH, tileTable);
        var tmp = current; current = other; other = tmp;
    }

    // blend an arbitrary doc-space sprite onto the accumulator with explicit blend/opacity
    // and a pixel offset (sample sprite at doc-offset). Used for layer-effect sprites.
    private void BlendBufferInto(ref Buffer* current, ref Buffer* other, Buffer* src,
        BlendMode mode, float opacity, float offX, float offY, Buffer* maskBuf)
    {
        Span<float> inv = stackalloc float[6] { 1, 0, 0, 1, -offX, -offY };
        WriteBlendParams((uint)mode, opacity, 0f, inv, 1f, maskBuf is not null);
        DispatchBlend(current, src, other, maskBuf, _width, _height);   // doc-space sprite
        var tmp = current; current = other; other = tmp;
    }

    // render a content layer into doc space (offset/transform + mask applied) → _fxLdoc, for FX source
    private void RasterizeLayerToDoc(Buffer* layerBuf, Layer layer, Buffer* maskBuf, int srcW, int srcH, Buffer* tileTable = null)
    {
        var (inv, persp) = LayerInverse(layer, srcW, srcH);
        ClearBuffer(_fxTint);   // transparent backdrop
        WriteBlendParams((uint)BlendMode.Normal, 1f, 0f, inv, 1f, maskBuf is not null, persp, tileTable is not null ? 1u : 0u);
        DispatchBlend(_fxTint, layerBuf, _fxLdoc, maskBuf, srcW, srcH, tileTable);
    }

    private void DispatchFx(Buffer* src, Buffer* outp, uint mode, float r, float g, float b, float size, float pos,
        float r2 = 0, float g2 = 0, float b2 = 0, float angle = 0, float offX = 0, float offY = 0)
    {
        var api = _gpu.Api;
        var prm = stackalloc float[12];
        ((uint*)prm)[0] = mode;
        prm[1] = r; prm[2] = g; prm[3] = b; prm[4] = size; prm[5] = pos;
        prm[6] = r2; prm[7] = g2; prm[8] = b2; prm[9] = angle; prm[10] = offX; prm[11] = offY;
        api.QueueWriteBuffer(_gpu.Queue, _fxParamsBuf, 0, prm, 48);

        var bg = stackalloc BindGroupEntry[4];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _fxParamsBuf, Size = 48 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = src, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = outp, Size = (ulong)_imgBytes };
        var bgDesc = new BindGroupDescriptor { Layout = _fxBgl, EntryCount = 4, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, _fxPipeline);
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

    // composite a content layer plus its non-destructive effects (shadow/glow behind, overlay/stroke front)
    private void BlendContentWithFx(ref Buffer* current, ref Buffer* other, Buffer* srcBuf, Layer layer, Buffer* maskBuf, int srcW, int srcH,
        Buffer* tileTable = null)
    {
        if (!layer.HasEffects)
        {
            BlendInto(ref current, ref other, srcBuf, layer, maskBuf, srcW, srcH, tileTable);
            return;
        }

        RasterizeLayerToDoc(srcBuf, layer, maskBuf, srcW, srcH, tileTable);   // _fxLdoc = layer in doc space

        // behind effects (drop shadow / outer glow), in Effects-list order
        foreach (var fx in layer.Effects)
            if (fx.Enabled && fx.Kind is LayerEffectKind.DropShadow or LayerEffectKind.OuterGlow)
            {
                DispatchFx(_fxLdoc, _fxTint, 0u, fx.R, fx.G, fx.B, 0f, 0f);   // tint = colour × layer alpha
                WriteBlurParams(fx.Radius, 1f, 0f); DispatchBlur(_fxTint, _fxBlur);
                WriteBlurParams(fx.Radius, 0f, 1f); DispatchBlur(_fxBlur, _fxTint);
                float ox = fx.Kind == LayerEffectKind.DropShadow ? fx.OffsetX : 0f;
                float oy = fx.Kind == LayerEffectKind.DropShadow ? fx.OffsetY : 0f;
                BlendBufferInto(ref current, ref other, _fxTint, fx.BlendMode, fx.Opacity, ox, oy, null);
            }

        // the layer itself
        BlendInto(ref current, ref other, srcBuf, layer, maskBuf, srcW, srcH, tileTable);

        // front effects, in Effects-list order (so list reordering changes the stacking)
        foreach (var fx in layer.Effects)
        {
            if (!fx.Enabled) continue;
            switch (fx.Kind)
            {
                case LayerEffectKind.ColorOverlay:
                    DispatchFx(_fxLdoc, _fxTint, 0u, fx.R, fx.G, fx.B, 0f, 0f);
                    break;
                case LayerEffectKind.GradientOverlay:
                    DispatchFx(_fxLdoc, _fxTint, 4u, fx.R, fx.G, fx.B, 0f, 0f, fx.R2, fx.G2, fx.B2, fx.Angle);
                    break;
                case LayerEffectKind.InnerShadow:
                    DispatchFx(_fxLdoc, _fxTint, 2u, fx.R, fx.G, fx.B, fx.Radius, 0f, 0, 0, 0, 0, fx.OffsetX, fx.OffsetY);
                    break;
                case LayerEffectKind.InnerGlow:
                    DispatchFx(_fxLdoc, _fxTint, 3u, fx.R, fx.G, fx.B, fx.Radius, 0f);
                    break;
                case LayerEffectKind.Bevel:
                    DispatchFx(_fxLdoc, _fxTint, 5u, fx.R, fx.G, fx.B, fx.Size, 0f, fx.R2, fx.G2, fx.B2, fx.Angle, fx.Depth);
                    break;
                case LayerEffectKind.Stroke:
                    DispatchFx(_fxLdoc, _fxTint, 1u, fx.R, fx.G, fx.B, fx.Size, (float)fx.StrokePos);
                    break;
                default:
                    continue;   // behind effects already handled
            }
            BlendBufferInto(ref current, ref other, _fxTint, fx.BlendMode, fx.Opacity, 0f, 0f, null);
        }
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
        var bg = stackalloc BindGroupEntry[6];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _adjParamsBuf, Size = 64 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = src, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = outp, Size = (ulong)_imgBytes };
        bg[4] = new BindGroupEntry { Binding = 4, Buffer = mask, Size = (ulong)_imgBytes };
        bg[5] = new BindGroupEntry { Binding = 5, Buffer = _curveLutBuf, Size = 4 * 256 * 4 };
        var bgDesc = new BindGroupDescriptor { Layout = _adjBgl, EntryCount = 6, Entries = bg };
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

    // grow the preview scratch buffer when the active layer is larger than the document
    private void EnsurePreviewBuffer(int bytes)
    {
        if (bytes <= _previewBytes) return;
        if (_previewBuf is not null) _gpu.Api.BufferRelease(_previewBuf);
        _previewBuf = NewBuffer(bytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _previewBytes = bytes;
    }

    private void CopyBuffer(Buffer* src, Buffer* dst, int? bytes = null)
    {
        var api = _gpu.Api;
        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        api.CommandEncoderCopyBufferToBuffer(encoder, src, 0, dst, 0, (ulong)(bytes ?? _imgBytes));
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
    }

    // buf/src are the layer's own buffer (size lw*lh*4); the dab centre is converted to buffer
    // space (doc centre minus the layer origin) so the preview lands correctly on an offset layer.
    private void DispatchStamp(Buffer* buf, Buffer* src, PreviewDab pv, int lw, int lh, int ox, int oy)
    {
        var api = _gpu.Api;
        // the stamp shader indexes by dims.width/height → set them to the layer buffer size
        var dimsv = stackalloc uint[4] { (uint)lw, (uint)lh, 0, 0 };
        api.QueueWriteBuffer(_gpu.Queue, _dimsBuf, 0, dimsv, 16);
        ulong bytes = (ulong)lw * (ulong)lh * 4;
        var prm = stackalloc float[12]
        {
            pv.Cx - ox, pv.Cy - oy, pv.Radius, pv.Hardness,
            pv.R / 255f, pv.G / 255f, pv.B / 255f, pv.Erase ? 1f : 0f,
            pv.IsClone ? 1f : 0f, pv.CloneOffX, pv.CloneOffY, 0f
        };
        api.QueueWriteBuffer(_gpu.Queue, _stampParamsBuf, 0, prm, 48);

        var bg = stackalloc BindGroupEntry[4];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _stampParamsBuf, Size = 48 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = buf, Size = bytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = src, Size = bytes };
        var bgDesc = new BindGroupDescriptor { Layout = _stampBgl, EntryCount = 4, Entries = bg };
        var bindGroup = api.DeviceCreateBindGroup(_gpu.Device, in bgDesc);

        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, _stampPipeline);
        api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        api.ComputePassEncoderDispatchWorkgroups(pass, (uint)((lw + 15) / 16), (uint)((lh + 15) / 16), 1);
        api.ComputePassEncoderEnd(pass);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
        api.QueueSubmit(_gpu.Queue, 1, &cmd);
        api.ComputePassEncoderRelease(pass);
        api.CommandEncoderRelease(encoder);
        api.CommandBufferRelease(cmd);
        api.BindGroupRelease(bindGroup);
    }

    private void WriteBlurParams(float radius, float dirX, float dirY, float box = 0f)
    {
        var prm = stackalloc float[4] { radius, dirX, dirY, box };
        _gpu.Api.QueueWriteBuffer(_gpu.Queue, _blurParamsBuf, 0, prm, 16);
    }

    // 32B shared filter params: mode(u32) + p0..p6
    private void WriteFilterParams(uint mode, float p0 = 0, float p1 = 0, float p2 = 0)
    {
        var prm = stackalloc float[8];
        ((uint*)prm)[0] = mode;
        prm[1] = p0; prm[2] = p1; prm[3] = p2;
        _gpu.Api.QueueWriteBuffer(_gpu.Queue, _filterParamsBuf, 0, prm, 32);
    }

    // single src→out filter pass (motion/zoom/sharpen/noise) on the 4-binding blur layout
    private void DispatchFilterPass(ComputePipeline* pipeline, Buffer* src, Buffer* outp)
    {
        var api = _gpu.Api;
        var bg = stackalloc BindGroupEntry[4];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _filterParamsBuf, Size = 32 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = src, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = outp, Size = (ulong)_imgBytes };
        var bgDesc = new BindGroupDescriptor { Layout = _blurBgl, EntryCount = 4, Entries = bg };
        DispatchPass(pipeline, api.DeviceCreateBindGroup(_gpu.Device, in bgDesc));
    }

    // unsharp/high-pass/clarity: combine src + blurred → out (5-binding layout)
    private void DispatchCombine(Buffer* src, Buffer* blurred, Buffer* outp)
    {
        var api = _gpu.Api;
        var bg = stackalloc BindGroupEntry[5];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _filterParamsBuf, Size = 32 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = src, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = blurred, Size = (ulong)_imgBytes };
        bg[4] = new BindGroupEntry { Binding = 4, Buffer = outp, Size = (ulong)_imgBytes };
        var bgDesc = new BindGroupDescriptor { Layout = _combineBgl, EntryCount = 5, Entries = bg };
        DispatchPass(_combinePipeline, api.DeviceCreateBindGroup(_gpu.Device, in bgDesc));
    }

    // run one compute pass over the whole image with a prepared bind group, then release it
    private void DispatchPass(ComputePipeline* pipeline, BindGroup* bindGroup)
    {
        var api = _gpu.Api;
        var encDesc = new CommandEncoderDescriptor();
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, in encDesc);
        var passDesc = new ComputePassDescriptor();
        var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
        api.ComputePassEncoderSetPipeline(pass, pipeline);
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

    // separable gaussian/box blur src→dst (via _filterTemp), box=1 for uniform weights
    private void BlurInto(Buffer* src, Buffer* dst, float radius, float box)
    {
        WriteBlurParams(radius, 1f, 0f, box); DispatchBlur(src, _filterTemp);
        WriteBlurParams(radius, 0f, 1f, box); DispatchBlur(_filterTemp, dst);
    }

    // produce the filtered backdrop into dst (dst must differ from src)
    private void RenderFilter(FilterLayer flt, Buffer* src, Buffer* dst)
    {
        switch (flt.Kind)
        {
            case FilterKind.GaussianBlur: BlurInto(src, dst, flt.Radius, 0f); break;
            case FilterKind.BoxBlur:      BlurInto(src, dst, flt.Radius, 1f); break;
            case FilterKind.MotionBlur:   WriteFilterParams(0u, flt.Radius, flt.Angle); DispatchFilterPass(_dirPipeline, src, dst); break;
            case FilterKind.ZoomBlur:     WriteFilterParams(1u, Math.Clamp(flt.Amount, 0f, 1f)); DispatchFilterPass(_dirPipeline, src, dst); break;
            case FilterKind.Sharpen:      WriteFilterParams(0u, flt.Amount); DispatchFilterPass(_convPipeline, src, dst); break;
            case FilterKind.UnsharpMask:  BlurInto(src, _fxBlur, flt.Radius, 0f); WriteFilterParams(0u, flt.Amount); DispatchCombine(src, _fxBlur, dst); break;
            case FilterKind.HighPass:     BlurInto(src, _fxBlur, flt.Radius, 0f); WriteFilterParams(1u); DispatchCombine(src, _fxBlur, dst); break;
            case FilterKind.Clarity:      BlurInto(src, _fxBlur, Math.Max(8f, flt.Radius), 0f); WriteFilterParams(2u, flt.Amount); DispatchCombine(src, _fxBlur, dst); break;
            case FilterKind.AddNoise:     WriteFilterParams(0u, flt.Amount, 1.7f); DispatchFilterPass(_noisePipeline, src, dst); break;
            case FilterKind.Denoise:      WriteFilterParams(1u, Math.Max(0.02f, flt.Amount)); DispatchFilterPass(_noisePipeline, src, dst); break;
            default:                      BlurInto(src, dst, flt.Radius, 0f); break;
        }
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

    // srcW/srcH = the source buffer's own dimensions (doc-sized for sprites/groups, the layer's
    // own size for a PixelLayer with independent bounds). The mask is sampled with the same
    // layer coords, so it must match the source layout (doc-sized white mask is uniform → safe).
    // mask == null → the layer has no mask (params.hasMask is 0); bind src as a harmless dummy so
    // the bind group is complete. A real mask is layer-aligned, so its size equals srcBytes.
    private void DispatchBlend(Buffer* dst, Buffer* src, Buffer* outp, Buffer* mask, int srcW, int srcH, Buffer* tileTable = null)
    {
        var api = _gpu.Api;
        // refresh dims with this layer's src size (output grid stays the document)
        var dimsv = stackalloc uint[4] { (uint)_width, (uint)_height, (uint)srcW, (uint)srcH };
        api.QueueWriteBuffer(_gpu.Queue, _dimsBuf, 0, dimsv, 16);
        ulong srcBytes = (ulong)srcW * (ulong)srcH * 4;
        bool atlas = tileTable is not null;
        // src (binding 3): real buffer in contiguous mode, a 4-byte dummy in atlas mode
        Buffer* srcBind = atlas ? _dummyStore : src;
        ulong srcSize = atlas ? 4 : srcBytes;
        // mask (binding 5): real layer-aligned mask, or a 4-byte dummy when the layer has none
        Buffer* maskBind = mask is not null ? mask : _dummyStore;
        ulong maskSize = mask is not null ? srcBytes : 4;
        var bg = stackalloc BindGroupEntry[8];
        bg[0] = new BindGroupEntry { Binding = 0, Buffer = _dimsBuf, Size = 16 };
        bg[1] = new BindGroupEntry { Binding = 1, Buffer = _paramsBuf, Size = 64 };
        bg[2] = new BindGroupEntry { Binding = 2, Buffer = dst, Size = (ulong)_imgBytes };
        bg[3] = new BindGroupEntry { Binding = 3, Buffer = srcBind, Size = srcSize };
        bg[4] = new BindGroupEntry { Binding = 4, Buffer = outp, Size = (ulong)_imgBytes };
        bg[5] = new BindGroupEntry { Binding = 5, Buffer = maskBind, Size = maskSize };
        bg[6] = new BindGroupEntry { Binding = 6, Buffer = atlas ? tileTable : _dummyStore, Size = atlas ? (ulong)_tileTableWords * 4 : 4 };
        bg[7] = new BindGroupEntry { Binding = 7, Buffer = atlas ? _atlasBuf : _dummyStore, Size = atlas ? _atlasBytes : 4 };
        var bgDesc = new BindGroupDescriptor { Layout = _bgl, EntryCount = 8, Entries = bg };
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
        int layerBytes = px.Width * px.Height * 4;
        bool cached = _layerBuffers.TryGetValue(px, out var existing);
        // a resized layer buffer (e.g. grown bounds) must be reallocated, not partially written
        if (cached && _layerBufferBytes.TryGetValue(px, out var cb) && cb != layerBytes)
        {
            _gpu.Api.BufferRelease((Buffer*)existing);
            _layerBuffers.Remove(px); _layerBufferBytes.Remove(px);
            cached = false; existing = 0;
        }
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
                fixed (byte* p = px.Pixels) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)layerBytes);
            }
        }
        else
        {
            buf = NewBuffer(layerBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
            _layerBuffers[px] = (nint)buf;
            _layerBufferBytes[px] = layerBytes;
            fixed (byte* p = px.Pixels) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)layerBytes);
        }

        // Coherence: this monolithic (preview) path is about to clear px.Dirty/DirtyTiles. Mirror the
        // edit into the atlas residency so a later non-preview frame's GetLayerTiles doesn't reuse stale
        // resident tiles (symmetric to GetLayerTiles dropping the stale monolithic buffer). Without this
        // the canvas flickers between new (monolithic) and old (atlas) as the brush preview toggles.
        if (_residency is not null && (px.Dirty || px.DirtyTiles.Count > 0))
        {
            if (px.DirtyTiles.Count > 0)
            {
                foreach (var (tx, ty) in px.DirtyTiles)
                {
                    _residency.Invalidate(new TileResidency.Key(px, tx, ty));
                    _emptyTiles.Remove((px, tx, ty));
                }
            }
            else
            {
                _residency.ReleaseOwner(px);   // bulk change, no tile info → drop all resident tiles
                RemoveEmpties(px);
            }
        }

        px.DirtyTiles.Clear();
        px.Dirty = false;
        return buf;
    }

    // ----- GPU-tiled storage (PLAN §3/§17.3) -----

    /// <summary>
    /// Ensure the layer's non-empty 256×256 tiles are resident in the shared atlas and (re)upload
    /// its tile table. Returns the table buffer, or null when the layer should use the monolithic
    /// buffer instead (more live tiles than the atlas can hold in one pass).
    /// </summary>
    private Buffer* GetLayerTiles(PixelLayer px)
    {
        if (_residency is null) return null;
        int gw = (px.Width + TileSz - 1) / TileSz;
        int gh = (px.Height + TileSz - 1) / TileSz;
        int tiles = gw * gh;
        if (tiles > _atlasSlots) return null;   // monster layer → monolithic fallback

        // resize or bulk change (Dirty with no tile info) → reset this layer's residency + emptiness
        bool resized = !_layerTileDims.TryGetValue(px, out var d) || d.w != px.Width || d.h != px.Height;
        // the atlas path clears px.Dirty; a monolithic buffer cached from a prior hover-preview would
        // then look up-to-date but miss this edit. Drop it so the next preview re-syncs from px.Pixels.
        if ((resized || px.Dirty) && _layerBuffers.TryGetValue(px, out var stale))
        {
            _gpu.Api.BufferRelease((Buffer*)stale);
            _layerBuffers.Remove(px);
            _layerBufferBytes.Remove(px);
        }
        if (resized || (px.Dirty && px.DirtyTiles.Count == 0))
        {
            _residency.ReleaseOwner(px);
            RemoveEmpties(px);
        }
        else
        {
            foreach (var (tx, ty) in px.DirtyTiles)
            {
                _residency.Invalidate(new TileResidency.Key(px, tx, ty));
                _emptyTiles.Remove((px, tx, ty));
            }
        }
        _layerTileDims[px] = (px.Width, px.Height);

        int need = 2 + tiles;
        if (need > _tileTableWords)
        {
            int n = _tileTableWords;
            while (n < need) n *= 2;
            _gpu.Api.BufferRelease(_tileTableBuf);
            _tileTableBuf = NewBuffer(n * 4, BufferUsage.Storage | BufferUsage.CopyDst);
            _tileTableWords = n;
            _tableScratch = new uint[n];
        }

        _tableScratch[0] = (uint)gw;
        _tableScratch[1] = (uint)gh;
        for (int ty = 0; ty < gh; ty++)
            for (int tx = 0; tx < gw; tx++)
            {
                int ti = 2 + ty * gw + tx;
                if (IsTileEmpty(px, tx, ty)) { _tableScratch[ti] = 0xFFFFFFFFu; continue; }
                int slot = _residency.Acquire(new TileResidency.Key(px, tx, ty), out bool needUpload);
                if (needUpload) UploadTile(px, tx, ty, slot);
                _tableScratch[ti] = (uint)slot;
            }

        fixed (uint* tp = _tableScratch)
            _gpu.Api.QueueWriteBuffer(_gpu.Queue, _tileTableBuf, 0, tp, (nuint)(need * 4));

        px.DirtyTiles.Clear();
        px.Dirty = false;
        return _tileTableBuf;
    }

    private void RemoveEmpties(Layer owner)
    {
        var drop = new List<(Layer, int, int)>();
        foreach (var k in _emptyTiles.Keys) if (ReferenceEquals(k.Item1, owner)) drop.Add(k);
        foreach (var k in drop) _emptyTiles.Remove(k);
    }

    /// <summary>A tile is empty (no slot needed) when every pixel's alpha is 0. Cached; invalidated on paint.</summary>
    private bool IsTileEmpty(PixelLayer px, int tx, int ty)
    {
        if (_emptyTiles.TryGetValue((px, tx, ty), out bool cached)) return cached;
        int tw = RasterTiles.TileWidth(px.Width, tx);
        int th = RasterTiles.TileHeight(px.Height, ty);
        var pix = px.Pixels;
        bool empty = true;
        for (int ry = 0; ry < th && empty; ry++)
        {
            int rowBase = ((ty * TileSz + ry) * px.Width + tx * TileSz) * 4;
            for (int rx = 0; rx < tw; rx++)
                if (pix[rowBase + rx * 4 + 3] != 0) { empty = false; break; }
        }
        _emptyTiles[(px, tx, ty)] = empty;
        return empty;
    }

    /// <summary>Upload one tile's rows from the layer buffer into its atlas slot (strided copy).</summary>
    private void UploadTile(PixelLayer px, int tx, int ty, int slot)
    {
        int tw = RasterTiles.TileWidth(px.Width, tx);
        int th = RasterTiles.TileHeight(px.Height, ty);
        ulong slotBase = (ulong)slot * TileBytes;
        fixed (byte* p = px.Pixels)
        {
            for (int ry = 0; ry < th; ry++)
            {
                int srcOff = ((ty * TileSz + ry) * px.Width + tx * TileSz) * 4;
                ulong dstOff = slotBase + (ulong)(ry * TileSz) * 4;
                _gpu.Api.QueueWriteBuffer(_gpu.Queue, _atlasBuf, dstOff, p + srcOff, (nuint)(tw * 4));
            }
        }
    }

    private byte[]? _shapeScratch;

    /// <summary>(Re)rasterize a parametric shape layer into a GPU buffer; cached, refreshed when dirty.</summary>
    private Buffer* GetShapeBuffer(ShapeLayer sh)
    {
        bool cached = _layerBuffers.TryGetValue(sh, out var existing);
        if (cached && !sh.Dirty) return (Buffer*)existing;

        Buffer* buf;
        if (cached) buf = (Buffer*)existing;
        else { buf = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc); _layerBuffers[sh] = (nint)buf; }

        if (_shapeScratch is null || _shapeScratch.Length != _imgBytes) _shapeScratch = new byte[_imgBytes];
        sh.Rasterize(_shapeScratch, _width, _height);
        fixed (byte* p = _shapeScratch) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)_imgBytes);
        sh.Dirty = false;
        return buf;
    }

    /// <summary>(Re)rasterize a parametric vector-path layer into a GPU buffer; cached, refreshed when dirty.</summary>
    private Buffer* GetPathBuffer(PathLayer pth)
    {
        bool cached = _layerBuffers.TryGetValue(pth, out var existing);
        if (cached && !pth.Dirty) return (Buffer*)existing;

        Buffer* buf;
        if (cached) buf = (Buffer*)existing;
        else { buf = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc); _layerBuffers[pth] = (nint)buf; }

        if (_shapeScratch is null || _shapeScratch.Length != _imgBytes) _shapeScratch = new byte[_imgBytes];
        pth.Rasterize(_shapeScratch, _width, _height);
        fixed (byte* p = _shapeScratch) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)_imgBytes);
        pth.Dirty = false;
        return buf;
    }

    /// <summary>(Re)rasterize a parametric text layer into a GPU buffer; cached, refreshed when dirty.</summary>
    private Buffer* GetTextBuffer(TextLayer txt)
    {
        bool cached = _layerBuffers.TryGetValue(txt, out var existing);
        if (cached && !txt.Dirty) return (Buffer*)existing;

        Buffer* buf;
        if (cached) buf = (Buffer*)existing;
        else { buf = NewBuffer(_imgBytes, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc); _layerBuffers[txt] = (nint)buf; }

        if (_shapeScratch is null || _shapeScratch.Length != _imgBytes) _shapeScratch = new byte[_imgBytes];
        txt.Rasterize(_shapeScratch, _width, _height);
        fixed (byte* p = _shapeScratch) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)_imgBytes);
        txt.Dirty = false;
        return buf;
    }

    // Returns the layer's mask GPU buffer (sized to the mask's own bytes, which match the layer
    // buffer for a pixel layer), or null when the layer has no mask. The mask is sampled with the
    // same layer coords as the source, so its size must equal the source's.
    private Buffer* GetMaskBuffer(Layer layer)
    {
        if (!layer.HasMask) return null;
        int maskBytes = layer.Mask!.Length;

        bool cached = _maskBuffers.TryGetValue(layer, out var existing);
        if (cached && _maskBufferBytes.TryGetValue(layer, out var cb) && cb != maskBytes)
        {
            _gpu.Api.BufferRelease((Buffer*)existing);
            _maskBuffers.Remove(layer); _maskBufferBytes.Remove(layer);
            cached = false; existing = 0;
        }
        Buffer* buf;
        if (cached && !layer.MaskDirty) return (Buffer*)existing;
        if (cached) buf = (Buffer*)existing;
        else
        {
            buf = NewBuffer(maskBytes, BufferUsage.Storage | BufferUsage.CopyDst);
            _maskBuffers[layer] = (nint)buf;
            _maskBufferBytes[layer] = maskBytes;
        }
        fixed (byte* p = layer.Mask!) _gpu.Api.QueueWriteBuffer(_gpu.Queue, buf, 0, p, (nuint)maskBytes);
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
        if (_fxLdoc is not null) { api.BufferRelease(_fxLdoc); _fxLdoc = null; }
        if (_fxTint is not null) { api.BufferRelease(_fxTint); _fxTint = null; }
        if (_fxBlur is not null) { api.BufferRelease(_fxBlur); _fxBlur = null; }
        if (_backdropCache is not null) { api.BufferRelease(_backdropCache); _backdropCache = null; }
        _cacheValid = false;
        _lastResult = null;
    }

    public void Dispose()
    {
        var api = _gpu.Api;
        foreach (var p in _layerBuffers.Values) api.BufferRelease((Buffer*)p);
        _layerBuffers.Clear();
        _layerBufferBytes.Clear();
        foreach (var p in _maskBuffers.Values) api.BufferRelease((Buffer*)p);
        _maskBuffers.Clear();
        _maskBufferBytes.Clear();
        if (_atlasBuf is not null) api.BufferRelease(_atlasBuf);
        if (_dummyStore is not null) api.BufferRelease(_dummyStore);
        if (_tileTableBuf is not null) api.BufferRelease(_tileTableBuf);
        ReleaseSizeResources();
        if (_dimsBuf is not null) api.BufferRelease(_dimsBuf);
        if (_paramsBuf is not null) api.BufferRelease(_paramsBuf);
        if (_adjParamsBuf is not null) api.BufferRelease(_adjParamsBuf);
        if (_curveLutBuf is not null) api.BufferRelease(_curveLutBuf);
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
        if (_fxPipeline is not null) api.ComputePipelineRelease(_fxPipeline);
        if (_fxBgl is not null) api.BindGroupLayoutRelease(_fxBgl);
        if (_fxParamsBuf is not null) api.BufferRelease(_fxParamsBuf);
        if (_dirPipeline is not null) api.ComputePipelineRelease(_dirPipeline);
        if (_convPipeline is not null) api.ComputePipelineRelease(_convPipeline);
        if (_noisePipeline is not null) api.ComputePipelineRelease(_noisePipeline);
        if (_combinePipeline is not null) api.ComputePipelineRelease(_combinePipeline);
        if (_combineBgl is not null) api.BindGroupLayoutRelease(_combineBgl);
        if (_filterParamsBuf is not null) api.BufferRelease(_filterParamsBuf);
    }
}

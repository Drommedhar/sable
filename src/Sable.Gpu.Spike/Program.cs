using Sable.Gpu;
using SkiaSharp;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

// =============================================================================
// Sable M0 spike #1 — prove the wgpu (WebGPU) compute path on this machine.
// Composites TWO RGBA8 layers with a Normal (src-over) blend in a WGSL compute
// shader, reads the result back, and writes spike_out.png.
//
// This de-risks: Silk.NET.WebGPU binding, adapter/device init, storage buffers,
// WGSL compute pipeline, dispatch, GPU->CPU readback. (Surface/Avalonia embed
// is the next slice.)
// =============================================================================

const int Width = 512;
const int Height = 512;
const int PixelCount = Width * Height;
const int ImgBytes = PixelCount * 4;

Console.WriteLine("Sable GPU spike — initializing wgpu...");

unsafe
{
    using var gpu = new WgpuDevice();
    Console.WriteLine($"Adapter: {gpu.DescribeAdapter()}");
    var api = gpu.Api;

    // --- CPU-side layer data ---------------------------------------------------
    // Bottom: opaque diagonal gradient (blue->green).
    // Top:    semi-transparent red disc centered in the image.
    var bottom = new byte[ImgBytes];
    var top = new byte[ImgBytes];
    var cx = Width / 2.0; var cy = Height / 2.0; var radius = Width * 0.30;
    for (int y = 0; y < Height; y++)
    for (int x = 0; x < Width; x++)
    {
        int i = (y * Width + x) * 4;
        // bottom gradient
        bottom[i + 0] = (byte)(40);                                   // R
        bottom[i + 1] = (byte)(255 * x / (double)Width);              // G
        bottom[i + 2] = (byte)(255 * y / (double)Height);             // B
        bottom[i + 3] = 255;                                          // A

        // top disc, ~60% alpha inside radius
        double dx = x - cx, dy = y - cy;
        bool inside = dx * dx + dy * dy <= radius * radius;
        top[i + 0] = 230;                                             // R
        top[i + 1] = 30;                                              // G
        top[i + 2] = 30;                                              // B
        top[i + 3] = inside ? (byte)153 : (byte)0;                    // A (~0.6)
    }

    // --- buffers ---------------------------------------------------------------
    Buffer* bottomBuf = CreateBuffer(api, gpu.Device, ImgBytes, BufferUsage.Storage | BufferUsage.CopyDst);
    Buffer* topBuf    = CreateBuffer(api, gpu.Device, ImgBytes, BufferUsage.Storage | BufferUsage.CopyDst);
    Buffer* outBuf    = CreateBuffer(api, gpu.Device, ImgBytes, BufferUsage.Storage | BufferUsage.CopySrc);
    Buffer* readBuf   = CreateBuffer(api, gpu.Device, ImgBytes, BufferUsage.MapRead | BufferUsage.CopyDst);
    Buffer* dimsBuf   = CreateBuffer(api, gpu.Device, 16, BufferUsage.Uniform | BufferUsage.CopyDst);

    fixed (byte* pBottom = bottom) api.QueueWriteBuffer(gpu.Queue, bottomBuf, 0, pBottom, (nuint)ImgBytes);
    fixed (byte* pTop = top)       api.QueueWriteBuffer(gpu.Queue, topBuf, 0, pTop, (nuint)ImgBytes);
    var dims = stackalloc uint[4] { Width, Height, 0, 0 };
    api.QueueWriteBuffer(gpu.Queue, dimsBuf, 0, dims, 16);

    // --- shader module ---------------------------------------------------------
    string wgsl = ShaderLibrary.Load("blend_normal");
    var codePtr = (byte*)SilkMarshal.StringToPtr(wgsl);
    var wgslDesc = new ShaderModuleWGSLDescriptor
    {
        Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
        Code = codePtr
    };
    var smDesc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDesc };
    var module = api.DeviceCreateShaderModule(gpu.Device, in smDesc);
    SilkMarshal.Free((nint)codePtr);
    if (module is null) throw new InvalidOperationException("wgpu: shader module creation failed.");

    // --- bind group layout (uniform + 3 storage) -------------------------------
    var bglEntries = stackalloc BindGroupLayoutEntry[4];
    bglEntries[0] = new BindGroupLayoutEntry
    {
        Binding = 0, Visibility = ShaderStage.Compute,
        Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform }
    };
    bglEntries[1] = new BindGroupLayoutEntry
    {
        Binding = 1, Visibility = ShaderStage.Compute,
        Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
    };
    bglEntries[2] = new BindGroupLayoutEntry
    {
        Binding = 2, Visibility = ShaderStage.Compute,
        Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
    };
    bglEntries[3] = new BindGroupLayoutEntry
    {
        Binding = 3, Visibility = ShaderStage.Compute,
        Buffer = new BufferBindingLayout { Type = BufferBindingType.Storage }
    };
    var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 4, Entries = bglEntries };
    var bgl = api.DeviceCreateBindGroupLayout(gpu.Device, in bglDesc);

    // --- bind group ------------------------------------------------------------
    var bgEntries = stackalloc BindGroupEntry[4];
    bgEntries[0] = new BindGroupEntry { Binding = 0, Buffer = dimsBuf,   Offset = 0, Size = 16 };
    bgEntries[1] = new BindGroupEntry { Binding = 1, Buffer = bottomBuf, Offset = 0, Size = (ulong)ImgBytes };
    bgEntries[2] = new BindGroupEntry { Binding = 2, Buffer = topBuf,    Offset = 0, Size = (ulong)ImgBytes };
    bgEntries[3] = new BindGroupEntry { Binding = 3, Buffer = outBuf,    Offset = 0, Size = (ulong)ImgBytes };
    var bgDesc = new BindGroupDescriptor { Layout = bgl, EntryCount = 4, Entries = bgEntries };
    var bindGroup = api.DeviceCreateBindGroup(gpu.Device, in bgDesc);

    // --- pipeline --------------------------------------------------------------
    var bglLocal = bgl;
    var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bglLocal };
    var pipelineLayout = api.DeviceCreatePipelineLayout(gpu.Device, in plDesc);

    var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
    var cpDesc = new ComputePipelineDescriptor
    {
        Layout = pipelineLayout,
        Compute = new ProgrammableStageDescriptor { Module = module, EntryPoint = entryPoint }
    };
    var pipeline = api.DeviceCreateComputePipeline(gpu.Device, in cpDesc);
    SilkMarshal.Free((nint)entryPoint);
    if (pipeline is null) throw new InvalidOperationException("wgpu: compute pipeline creation failed.");

    // --- encode + dispatch -----------------------------------------------------
    var encDesc = new CommandEncoderDescriptor();
    var encoder = api.DeviceCreateCommandEncoder(gpu.Device, in encDesc);

    var passDesc = new ComputePassDescriptor();
    var pass = api.CommandEncoderBeginComputePass(encoder, in passDesc);
    api.ComputePassEncoderSetPipeline(pass, pipeline);
    api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
    uint groupsX = (uint)((Width + 15) / 16);
    uint groupsY = (uint)((Height + 15) / 16);
    api.ComputePassEncoderDispatchWorkgroups(pass, groupsX, groupsY, 1);
    api.ComputePassEncoderEnd(pass);

    api.CommandEncoderCopyBufferToBuffer(encoder, outBuf, 0, readBuf, 0, (ulong)ImgBytes);

    var cmdDesc = new CommandBufferDescriptor();
    var cmd = api.CommandEncoderFinish(encoder, in cmdDesc);
    api.QueueSubmit(gpu.Queue, 1, &cmd);

    // --- readback --------------------------------------------------------------
    bool mapped = false;
    var mapCb = PfnBufferMapCallback.From((status, _) =>
    {
        if (status != BufferMapAsyncStatus.Success)
            throw new InvalidOperationException($"wgpu: buffer map failed: {status}");
        mapped = true;
    });
    api.BufferMapAsync(readBuf, MapMode.Read, 0, (nuint)ImgBytes, mapCb, null);
    while (!mapped) gpu.Poll(wait: true);

    var src = (byte*)api.BufferGetMappedRange(readBuf, 0, (nuint)ImgBytes);
    var result = new byte[ImgBytes];
    new ReadOnlySpan<byte>(src, ImgBytes).CopyTo(result);
    api.BufferUnmap(readBuf);

    // --- save PNG --------------------------------------------------------------
    var outPath = Path.GetFullPath("spike_out.png");
    using (var bmp = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
    {
        System.Runtime.InteropServices.Marshal.Copy(result, 0, bmp.GetPixels(), ImgBytes);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(outPath);
        data.SaveTo(fs);
    }

    // sanity: sample center pixel (should be the blended red over gradient)
    int ci = ((Height / 2) * Width + (Width / 2)) * 4;
    Console.WriteLine($"Center pixel RGBA = ({result[ci]},{result[ci+1]},{result[ci+2]},{result[ci+3]})");
    Console.WriteLine($"OK -> {outPath}");

    // --- M1 verification: real Document -> GpuCompositor -> readback -> PNG -----
    Console.WriteLine("M1: compositing demo Document...");
    var doc = Sable.Engine.Document.CreateDemo(643, 360);   // non-256-aligned width on purpose
    doc.Layers[1].OffsetX = 40; doc.Layers[1].OffsetY = 20;   // translate
    doc.Layers[1].Rotation = 20f; doc.Layers[1].ScaleX = 1.3f; doc.Layers[1].ScaleY = 1.3f;   // rotate+scale (affine)
    doc.Layers[1].ShearX = 0.3f;   // + horizontal shear (skew)
    // perspective GPU smoke: free-corner homography on a small layer
    {
        var pl = new Sable.Engine.Layers.PixelLayer(80, 80, "persp");
        var pp = pl.Pixels;
        for (int i = 0; i < 80 * 80; i++) { pp[i * 4] = 80; pp[i * 4 + 1] = 200; pp[i * 4 + 2] = 255; pp[i * 4 + 3] = 255; }
        pl.Perspective = true;
        pl.PerspCorners = new float[] { 480, 30, 600, 60, 580, 150, 470, 120 };   // TL,TR,BR,BL (doc px)
        doc.Layers.Add(pl);
    }
    // layer-effects GPU smoke: exercise the full BlendContentWithFx path (shadow/glow/stroke/overlay)
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.DropShadow));
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.OuterGlow));
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.Stroke));
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.ColorOverlay));
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.InnerShadow));
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.InnerGlow));
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.GradientOverlay));
    doc.Layers[1].Effects.Add(Sable.Engine.Layers.LayerEffect.Create(Sable.Engine.Layers.LayerEffectKind.Bevel));
    // live-filter GPU smoke: one of every FilterKind (exercises all filter pipelines + mask/opacity blend)
    foreach (Sable.Engine.Layers.FilterKind fk in System.Enum.GetValues<Sable.Engine.Layers.FilterKind>())
        doc.Layers.Add(new Sable.Engine.Layers.FilterLayer(fk) { Radius = 6f, Amount = 1f, Angle = 30f, Opacity = 0.7f });
    // vector path GPU smoke: a closed filled+stroked bézier diamond (exercises GetPathBuffer + raster)
    doc.Layers.Add(new Sable.Engine.Layers.PathLayer(new[]
    {
        new Sable.Engine.Layers.PathNode(doc.Width * 0.5f, doc.Height * 0.2f),
        new Sable.Engine.Layers.PathNode(doc.Width * 0.8f, doc.Height * 0.5f),
        new Sable.Engine.Layers.PathNode(doc.Width * 0.5f, doc.Height * 0.8f),
        new Sable.Engine.Layers.PathNode(doc.Width * 0.2f, doc.Height * 0.5f),
    }, true, 240, 180, 40) { Stroked = true, StrokeR = 20, StrokeG = 20, StrokeB = 20, StrokeWidth = 5f, Opacity = 0.8f });
    // shape GPU smoke: rounded rect (fill+dashed stroke), polygon, star, arrow
    doc.Layers.Add(new Sable.Engine.Layers.ShapeLayer(Sable.Engine.Layers.ShapeKind.RoundedRect, 20, 20, 120, 80, 60, 140, 220)
    { CornerRadius = 22, Stroked = true, StrokeR = 255, StrokeG = 255, StrokeB = 255, StrokeWidth = 4, DashOn = true, DashLen = 14, GapLen = 8, Opacity = 0.85f });
    doc.Layers.Add(new Sable.Engine.Layers.ShapeLayer(Sable.Engine.Layers.ShapeKind.Polygon, 170, 20, 100, 100, 220, 120, 60)
    { Sides = 6, Stroked = true, StrokeR = 20, StrokeG = 20, StrokeB = 20, StrokeWidth = 6, Join = Sable.Engine.Layers.LineJoin.Miter });
    // stroke join/cap smoke: thick zigzag, miter joins + square caps
    doc.Layers.Add(new Sable.Engine.Layers.PathLayer(new[]
    {
        new Sable.Engine.Layers.PathNode(30, 300), new Sable.Engine.Layers.PathNode(70, 250),
        new Sable.Engine.Layers.PathNode(110, 320), new Sable.Engine.Layers.PathNode(150, 250),
    }, false, 0, 0, 0)
    { Filled = false, Stroked = true, StrokeR = 90, StrokeG = 200, StrokeB = 120, StrokeWidth = 12,
      Cap = Sable.Engine.Layers.LineCap.Square, Join = Sable.Engine.Layers.LineJoin.Miter });
    doc.Layers.Add(new Sable.Engine.Layers.ShapeLayer(Sable.Engine.Layers.ShapeKind.Star, 300, 20, 110, 110, 250, 210, 40)
    { Sides = 5, InnerRatio = 0.45f, Stroked = true, StrokeR = 120, StrokeG = 80, StrokeB = 0, StrokeWidth = 2 });
    doc.Layers.Add(new Sable.Engine.Layers.ShapeLayer(Sable.Engine.Layers.ShapeKind.Arrow, 440, 60, 160, 40, 0, 0, 0)
    { StrokeR = 230, StrokeG = 40, StrokeB = 90, StrokeWidth = 6 });
    // text depth GPU smoke: text→curves (vector "Sa" with counters), on-path text, area-wrap text
    var tc = new Sable.Engine.Layers.TextLayer("Sa", 20, 240, 80, 255, 230, 120).ToPath();
    doc.Layers.Add(tc);
    var onpath = new Sable.Engine.Layers.TextLayer("on path text", 0, 0, 26, 120, 220, 255)
    { PathPoints = { (320, 300), (400, 260), (480, 300), (560, 260) } };
    doc.Layers.Add(onpath);
    doc.Layers.Add(new Sable.Engine.Layers.TextLayer("area text that wraps to the box width", 200, 200, 18, 255, 255, 255)
    { BoxWidth = 110, Tracking = 1 });
    // nested group smoke: wrap a pixel layer in a group so the recursive compositor runs
    var grp = new Sable.Engine.Layers.GroupLayer("grp") { Opacity = 0.6f };
    grp.Children.Add(new Sable.Engine.Layers.PixelLayer(doc.Width, doc.Height, "in-group"));
    doc.Layers.Add(grp);
    using (var compositor = new Sable.Engine.Compositing.GpuCompositor(gpu))
    {
        var flat = compositor.CompositeToBytes(doc);
        var m1Path = Path.GetFullPath("m1_export.png");
        Sable.Engine.IO.DocumentIO.ExportPng(m1Path, doc.Width, doc.Height, flat);
        Console.WriteLine($"M1 OK ({doc.Width}x{doc.Height}, {doc.Layers.Count} layers) -> {m1Path}");
    }
    // (pure-logic verification lives in tests/Sable.Tests — this spike is a GPU smoke test.)

    // --- dynamic layer bounds: composite a sub-document layer placed at an offset ---
    {
        var bdoc = new Sable.Engine.Document(64, 64);
        bdoc.Layers.Add(new Sable.Engine.Layers.PixelLayer(64, 64, "bg"));   // transparent bg
        var small = new Sable.Engine.Layers.PixelLayer(16, 16, "red") { OffsetX = 24, OffsetY = 24 };
        for (int i = 0; i < small.Pixels.Length; i += 4)
        { small.Pixels[i] = 255; small.Pixels[i + 1] = 0; small.Pixels[i + 2] = 0; small.Pixels[i + 3] = 255; }
        // layer-aligned mask: hide the left half of the 16x16 square
        small.AddWhiteMask(16, 16);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 8; x++) { int mi = (y * 16 + x) * 4; small.Mask![mi] = 0; }
        small.MaskDirty = true;
        bdoc.Layers.Add(small);
        using var bcomp = new Sable.Engine.Compositing.GpuCompositor(gpu);
        var px = bcomp.CompositeToBytes(bdoc);
        int Idx(int x, int y) => (y * 64 + x) * 4;
        // square spans doc [24,40); mask hides its left half (layer x<8 → doc x<32)
        int cRight = Idx(36, 31);   // right half: visible red
        int cLeft = Idx(27, 31);    // left half: masked out (transparent)
        int cOut = Idx(4, 4);       // outside the square: transparent
        bool ok = px[cRight] == 255 && px[cRight + 3] == 255 && px[cLeft + 3] == 0 && px[cOut + 3] == 0;
        Console.WriteLine($"sub-doc masked layer: right=({px[cRight]},{px[cRight+1]},{px[cRight+2]},{px[cRight+3]}) " +
            $"leftA={px[cLeft+3]} outsideA={px[cOut+3]} ok={ok}");

        // live preview dab on the offset layer (DispatchStamp in buffer space) — must not crash + must mark a pixel.
        // place it on the visible right half (doc 36,36 → buffer 12,12, mask=1)
        bcomp.Preview = new Sable.Engine.Compositing.PreviewDab(small, 36f, 36f, 2f, 1f, 0, 255, 0, false);
        var pv2 = bcomp.CompositeToBytes(bdoc);
        int cPrev = Idx(36, 36);   // green dab centre over the red square
        bool prevOk = pv2[cPrev + 1] > 200 && pv2[cPrev] < 60 && pv2[cPrev + 3] > 0;
        Console.WriteLine($"offset-layer preview dab: centre=({pv2[cPrev]},{pv2[cPrev+1]},{pv2[cPrev+2]},{pv2[cPrev+3]}) ok={prevOk}");
        bcomp.Preview = null;
    }

    // --- nested child filter (Affinity model): a Gaussian blur INSIDE a pixel layer blurs ONLY that layer ---
    {
        var ndoc = new Sable.Engine.Document(64, 64);
        ndoc.Layers.Add(new Sable.Engine.Layers.PixelLayer(64, 64, "bg"));   // transparent backdrop
        var host = new Sable.Engine.Layers.PixelLayer(64, 64, "host");
        for (int y = 24; y < 40; y++)                                        // opaque white square [24,40)
        for (int x = 24; x < 40; x++) { int i = (y * 64 + x) * 4; host.Pixels[i] = 255; host.Pixels[i + 1] = 255; host.Pixels[i + 2] = 255; host.Pixels[i + 3] = 255; }
        host.Children.Add(new Sable.Engine.Layers.FilterLayer(Sable.Engine.Layers.FilterKind.GaussianBlur) { Radius = 6f });
        ndoc.Layers.Add(host);
        using var ncomp = new Sable.Engine.Compositing.GpuCompositor(gpu);
        var np = ncomp.CompositeToBytes(ndoc);
        int Idx(int x, int y) => (y * 64 + x) * 4;
        int edge = Idx(43, 31);     // just outside the square edge (x=39): blur bleeds partial alpha
        int inner = Idx(38, 31);    // just INSIDE the edge: REPLACE softens it (<255). over-bug left 255
        int far = Idx(2, 2);        // far away: stays transparent (filter is clipped to the layer)
        int centre = Idx(31, 31);   // deep interior: stays ~opaque
        bool nok = np[edge + 3] > 0 && np[edge + 3] < 255 && np[inner + 3] > 0 && np[inner + 3] < 250
                   && np[far + 3] == 0 && np[centre + 3] > 200;
        Console.WriteLine($"nested filter blur (replace): edgeA={np[edge+3]} innerA={np[inner+3]} farA={np[far+3]} centreA={np[centre+3]} ok={nok}");
    }

    // --- tiled storage: dirty-tile atlas re-upload across composites (PLAN §3) ---
    {
        var tdoc = new Sable.Engine.Document(300, 300);   // 2x2 tile grid (256-tiled)
        var lay = new Sable.Engine.Layers.PixelLayer(300, 300, "paint");
        tdoc.Layers.Add(lay);
        using var tcomp = new Sable.Engine.Compositing.GpuCompositor(gpu);
        int TIdx(int x, int y) => (y * 300 + x) * 4;

        // first composite: layer is fully transparent → all tiles empty
        var t0 = tcomp.CompositeToBytes(tdoc);
        bool emptyOk = t0[TIdx(10, 10) + 3] == 0 && t0[TIdx(280, 280) + 3] == 0;

        // paint a blue block in tile (0,0) AND a block in tile (1,1); mark those tiles dirty
        void Paint(int x0, int y0, int s, byte r, byte g, byte b)
        {
            for (int y = y0; y < y0 + s; y++)
                for (int x = x0; x < x0 + s; x++)
                { int i = (y * 300 + x) * 4; lay.Pixels[i] = r; lay.Pixels[i+1] = g; lay.Pixels[i+2] = b; lay.Pixels[i+3] = 255; }
        }
        Paint(8, 8, 16, 0, 0, 255);        // tile (0,0)
        Paint(264, 264, 16, 0, 255, 0);    // tile (1,1)
        lay.MarkTilesDirty(new[] { (0, 0), (1, 1) });
        lay.Dirty = true;

        var t1 = tcomp.CompositeToBytes(tdoc);
        int a = TIdx(12, 12), c = TIdx(268, 268), e = TIdx(150, 150);
        bool paintOk = t1[a+2] == 255 && t1[a+3] == 255            // blue appeared (tile 0,0)
                    && t1[c+1] == 255 && t1[c+3] == 255            // green appeared (tile 1,1)
                    && t1[e+3] == 0;                               // untouched tile still empty
        Console.WriteLine($"tiled re-upload: emptyFirst={emptyOk} blue=({t1[a]},{t1[a+1]},{t1[a+2]},{t1[a+3]}) " +
            $"green=({t1[c]},{t1[c+1]},{t1[c+2]},{t1[c+3]}) midA={t1[e+3]} ok={emptyOk && paintOk}");
    }

    // --- composite-cache: fast path (cached backdrop below active) == full recomposite ---
    {
        var cdoc = new Sable.Engine.Document(64, 64);
        Sable.Engine.Layers.PixelLayer Mk(string n, byte r, byte g, byte b, byte a)
        {
            var l = new Sable.Engine.Layers.PixelLayer(64, 64, n);
            for (int i = 0; i < l.Pixels.Length; i += 4) { l.Pixels[i] = r; l.Pixels[i+1] = g; l.Pixels[i+2] = b; l.Pixels[i+3] = a; }
            return l;
        }
        var L0 = Mk("bg", 200, 30, 30, 255);
        var L1 = Mk("mid", 30, 200, 30, 128);
        var L2 = Mk("top", 30, 30, 200, 128);
        cdoc.Layers.Add(L0); cdoc.Layers.Add(L1); cdoc.Layers.Add(L2);
        using var cc = new Sable.Engine.Compositing.GpuCompositor(gpu);

        cc.CacheHintLayer = null;                          // reference: always full walk
        var refB = (byte[])cc.CompositeToBytes(cdoc).Clone();

        cc.CacheHintLayer = L2;                            // build the backdrop cache (below L2 = L0+L1)
        cc.CompositeToBytes(cdoc);
        L2.Dirty = true;                                   // only the active layer dirty → fast path
        var fastB = cc.CompositeToBytes(cdoc);

        bool same = refB.Length == fastB.Length;
        if (same) for (int i = 0; i < refB.Length; i++) if (refB[i] != fastB[i]) { same = false; break; }
        int mid = (32 * 64 + 32) * 4;
        Console.WriteLine($"composite-cache: fast==full={same} px=({fastB[mid]},{fastB[mid+1]},{fastB[mid+2]},{fastB[mid+3]})");
    }

    // --- M1 verification: .sable save/load round-trip ---------------------------
    var sdoc = Sable.Engine.Document.CreateDemo(80, 60);
    var sablePath = Path.GetFullPath("roundtrip.sable");
    Sable.Format.SableFile.Save(sdoc, sablePath);
    var loaded = Sable.Format.SableFile.Load(sablePath);
    var srcL0 = (Sable.Engine.Layers.PixelLayer)sdoc.Layers[0];
    var dstL0 = (Sable.Engine.Layers.PixelLayer)loaded.Layers[0];
    bool pxMatch = srcL0.Pixels[2000] == dstL0.Pixels[2000] && srcL0.Pixels[2003] == dstL0.Pixels[2003];
    Console.WriteLine($"sable roundtrip: {loaded.Width}x{loaded.Height} layers={loaded.Layers.Count} " +
        $"blend[2]={loaded.Layers[2].BlendMode} op[2]={loaded.Layers[2].Opacity} pxMatch={pxMatch}");
    File.Delete(sablePath);
}

static unsafe Buffer* CreateBuffer(WebGPU api, Device* device, int size, BufferUsage usage)
{
    var desc = new BufferDescriptor { Size = (ulong)size, Usage = usage, MappedAtCreation = false };
    var buf = api.DeviceCreateBuffer(device, in desc);
    if (buf is null) throw new InvalidOperationException("wgpu: buffer creation failed.");
    return buf;
}

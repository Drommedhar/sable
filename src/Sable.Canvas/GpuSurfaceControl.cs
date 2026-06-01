using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Sable.Engine;
using Sable.Engine.Compositing;
using Sable.Gpu;
using Silk.NET.WebGPU;

namespace Sable.Canvas;

/// <summary>
/// Embeds a live wgpu (WebGPU) swapchain inside the Avalonia visual tree via
/// <see cref="NativeControlHost"/>. Realises the GPU-first invariant: the canvas
/// is a native GPU surface composited into the Avalonia chrome, NOT drawn by
/// Avalonia/Skia (PLAN §2.1, §3).
///
/// Pipeline per frame: <see cref="GpuCompositor"/> recomposites the document (only
/// when dirty), and <see cref="SurfaceBlitter"/> presents the composite into the
/// swapchain. Currently stretches the composite to the surface (aspect-fit later).
///
/// Surface + input come from the OS backend (<see cref="Platform.CanvasPlatform"/>):
/// Windows (HWND), macOS (CAMetalLayer) and Linux (X11/Xlib, via XWayland on Wayland
/// sessions) are all wired.
/// </summary>
public sealed unsafe partial class GpuSurfaceControl : NativeControlHost
{
    private WgpuDevice? _gpu;
    private GpuCompositor? _compositor;
    private SurfaceBlitter? _blitter;
    private Document? _doc;
    private TextureView* _compositeView;

    private Surface* _surface;
    // pasteboard (canvas surround) colour, 0..1, set by the chrome theme. Defaults to dark.
    private float _pasteR = 0.16f, _pasteG = 0.16f, _pasteB = 0.17f;
    private TextureFormat _format = TextureFormat.Bgra8Unorm;

    /// <summary>Sets the pasteboard (surround) colour from the active chrome theme (0..255).</summary>
    public void SetPasteboardColor(byte r, byte g, byte b)
    {
        _pasteR = r / 255f; _pasteG = g / 255f; _pasteB = b / 255f;
    }

    // customisable canvas-overlay colours (0..1), set from settings; defaults match the built-ins.
    private bool _overlayColorsSet;
    private float _guideR = 0f, _guideG = 0.63f, _guideB = 0.9f;
    private float _smartR = 1f, _smartG = 0.2f, _smartB = 0.6f;
    private float _gridR = 0.5f, _gridG = 0.5f, _gridB = 0.5f;
    private float _qmR = 0.95f, _qmG = 0.1f, _qmB = 0.2f;

    /// <summary>Sets the customisable overlay colours (guide / smart-guide / grid / quick-mask) from settings, 0..255.</summary>
    public void SetOverlayColors(
        (byte R, byte G, byte B) guide, (byte R, byte G, byte B) smart,
        (byte R, byte G, byte B) grid, (byte R, byte G, byte B) quickMask)
    {
        _guideR = guide.R / 255f; _guideG = guide.G / 255f; _guideB = guide.B / 255f;
        _smartR = smart.R / 255f; _smartG = smart.G / 255f; _smartB = smart.B / 255f;
        _gridR = grid.R / 255f; _gridG = grid.G / 255f; _gridB = grid.B / 255f;
        _qmR = quickMask.R / 255f; _qmG = quickMask.G / 255f; _qmB = quickMask.B / 255f;
        _overlayColorsSet = true;
    }
    private DispatcherTimer? _timer;
    private nint _hwnd;
    private uint _width = 1, _height = 1;
    private bool _configured;
    private Sable.Engine.Compositing.PreviewDab? _lastPreview;

    // selection-mask overlay texture (R8, doc-sized) for edge marching-ants
    private Texture* _selMaskTex;
    private TextureView* _selMaskView;
    private int _selMaskTexW, _selMaskTexH, _selMaskVer = -1;

    /// <summary>Quick-mask mode (Q): paint the selection as a red rubylith with the brush. PLAN §3.</summary>
    public bool QuickMask { get; private set; }
    private byte[]? _qmask;          // editable RGBA8 doc-sized quick mask (R = coverage)
    private byte[]? _qmaskEntrySel;  // selection snapshot on entry, to restore on cancel

    /// <summary>Toggle quick-mask mode: enter seeds from the current selection; exit commits it.</summary>
    public void ToggleQuickMask()
    {
        if (_doc is null) return;
        if (!QuickMask)
        {
            int n = _doc.Width * _doc.Height;
            _qmask = new byte[n * 4];
            _qmaskEntrySel = _doc.SnapshotSelectionMask();   // remember for cancel
            if (_qmaskEntrySel is { } sel)
                for (int i = 0; i < n; i++) { _qmask[i * 4] = sel[i]; _qmask[i * 4 + 3] = 255; }
            QuickMask = true;
            SyncQuickMask();
        }
        else
        {
            if (_qmask is not null) _doc.SetMaskSelection(ExtractR(_qmask, _doc.Width, _doc.Height));   // commit
            QuickMask = false; _qmask = null; _qmaskEntrySel = null;
        }
    }

    /// <summary>Exit quick mask WITHOUT committing — restore the selection that existed on entry (Esc).</summary>
    public void CancelQuickMask()
    {
        if (!QuickMask || _doc is null) return;
        QuickMask = false; _qmask = null;
        if (_qmaskEntrySel is { } sel) _doc.SetMaskSelection(sel);
        else _doc.ClearSelection();
        _qmaskEntrySel = null;
    }

    private void SyncQuickMask()
    {
        if (_qmask is not null && _doc is not null)
            _doc.SetSelectionMaskLive(ExtractR(_qmask, _doc.Width, _doc.Height));
    }

    private static byte[] ExtractR(byte[] rgba, int w, int h)
    {
        var m = new byte[w * h];
        for (int i = 0; i < w * h; i++) m[i] = rgba[i * 4];
        return m;
    }

    /// <summary>Show the document grid (spacing in doc px) — toggled from View ▸ Show Grid.</summary>
    public bool ShowGrid { get; set; }
    public float GridSpacing { get; set; } = 50f;
    /// <summary>Show a 1px pixel grid when zoomed in far enough.</summary>
    public bool ShowPixelGrid { get; set; } = true;

    // viewport: _zoom = 1 means fit-to-window; pan in surface pixels
    private double _zoom = 1.0;
    private double _panX, _panY;
    private double _lastMouseX, _lastMouseY;   // surface px, tracked from WndProc

    /// <summary>Raised when the view transform (zoom/pan) changes — for the status-bar zoom readout.</summary>
    public event Action? ViewChanged;

    /// <summary>Raised on pointer move with the document-space cursor position — for the status bar.</summary>
    public event Action<double, double>? CursorDocMoved;

    /// <summary>Effective on-screen scale (screen pixels per document pixel); 1.0 = 100%.</summary>
    public double EffectiveScale => ComputeViewport().Scale;

    /// <summary>
    /// Viewport mapping in the control's own DIP space (device px ÷ RenderScaling): document
    /// top-left offset + scale. The rulers use this to place ticks flush with the GPU surface.
    /// </summary>
    public (double ox, double oy, double scale) ViewportDip
    {
        get
        {
            var v = ComputeViewport();
            double s = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            if (s <= 0) s = 1.0;
            return (v.Ox / s, v.Oy / s, v.Scale / s);
        }
    }

    /// <summary>Zoom about the surface center (keyboard).</summary>
    public void ZoomBy(double factor) => ZoomAt(factor, _width / 2.0, _height / 2.0);

    /// <summary>Zoom keeping the document point under (sx,sy) fixed (mouse wheel).</summary>
    public void ZoomAt(double factor, double sx, double sy)
    {
        var old = ComputeViewport();
        if (old.Scale <= 0) { _zoom = Math.Clamp(_zoom * factor, 0.05, 64.0); ViewChanged?.Invoke(); return; }

        double docX = (sx - old.Ox) / old.Scale;
        double docY = (sy - old.Oy) / old.Scale;

        _zoom = Math.Clamp(_zoom * factor, 0.05, 64.0);

        double dw = _doc?.Width ?? 1, dh = _doc?.Height ?? 1;
        float fit = Math.Min(_width / (float)dw, _height / (float)dh);
        double newScale = fit * _zoom;
        // solve pan so (docX,docY) maps back to (sx,sy)
        _panX = sx - docX * newScale - (_width - dw * newScale) / 2.0;
        _panY = sy - docY * newScale - (_height - dh * newScale) / 2.0;
        ViewChanged?.Invoke();
    }

    public void PanBy(double dx, double dy)
    {
        _panX += dx;
        _panY += dy;
        ViewChanged?.Invoke();
    }

    public void ResetView()
    {
        _zoom = 1.0; _panX = 0; _panY = 0;
        ViewChanged?.Invoke();
    }

    /// <summary>Set the on-screen zoom to <paramref name="percent"/>% (about the surface centre).</summary>
    public void SetZoomPercent(double percent)
    {
        double cur = EffectiveScale;
        if (cur <= 0) return;
        ZoomAt((percent / 100.0) / cur, _width / 2.0, _height / 2.0);
    }

    /// <summary>Zoom to 100% (1 doc px = 1 screen px), about the surface centre.</summary>
    public void ZoomActualPixels() => SetZoomPercent(100);

    /// <summary>Fit the document to the window; if <paramref name="limitTo100"/>, never zoom past 100% (1 doc px = 1 screen px).</summary>
    public void FitView(bool limitTo100)
    {
        _zoom = 1.0; _panX = 0; _panY = 0;
        if (limitTo100)
        {
            var vp = ComputeViewport();          // scale = fit (since _zoom == 1)
            if (vp.Scale > 1.0) _zoom = 1.0 / vp.Scale;   // cap effective scale at 1.0
        }
        ViewChanged?.Invoke();
    }

    // OS-specific canvas bits (surface creation, timer resolution) live behind the backend;
    // everything else in this control is shared across platforms (PLAN §2.1/§2.2).
    private IDisposable? _timerResToken;

    /// <summary>The document currently shown. Set to swap what the canvas renders.</summary>
    public Document? Document
    {
        get => _doc;
        set
        {
            if (ReferenceEquals(_doc, value)) return;
            _compositor?.ReleaseLayerCaches();   // free the old doc's cached GPU buffers (no leak on swap)
            _doc = value;
            _compositeView = null;
            SetSmartObjects(null);   // smart-select masks belong to the old doc's layer — drop them on swap
        }
    }

    /// <summary>Flatten the current document to RGBA8 on the GPU and read it back (for export).</summary>
    public byte[]? ReadComposite()
    {
        if (_compositor is null || _doc is null) return null;
        _compositor.Preview = null;   // never bake the live brush preview into an export
        return _compositor.CompositeToBytes(_doc);
    }

    /// <summary>Composite an arbitrary set of layers (doc-sized) to RGBA8 — for merge/flatten/rasterise.</summary>
    public byte[]? RenderLayersToPixels(System.Collections.Generic.List<Sable.Engine.Layers.Layer> layers)
    {
        if (_compositor is null || _doc is null || layers.Count == 0) return null;
        _compositor.Preview = null;
        var tmp = new Document(_doc.Width, _doc.Height);
        tmp.Layers.AddRange(layers);
        var bytes = _compositor.CompositeToBytes(tmp);
        // CompositeToBytes re-presented the TEMP (subset) doc to the on-screen composite texture; if the
        // real doc isn't dirty (e.g. Smart Select just reads pixels), the render loop would stay stuck on it.
        // Force the next frame to recomposite the real document.
        _compositeView = null;
        return bytes;
    }

    private ViewportTransform ComputeViewport()
        => ViewportTransform.Fit(_width, _height, _doc?.Width ?? 1, _doc?.Height ?? 1, _zoom, _panX, _panY);

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _hwnd = handle.Handle;

        var backend = Platform.CanvasPlatform.Current;
        _timerResToken = backend.RaiseTimerResolution();
        InitGpu();   // creates the wgpu surface via the OS backend (no-op surface where unsupported)

        // OS input source feeds the shared ICanvasInputSink (this control); tool logic is OS-agnostic.
        _input = backend.CreateInput();
        _input.Attach(_hwnd, this);

        // Render priority (not the default Background) so the present tick isn't starved
        // behind input/layout — that was pinning the canvas at ~30fps.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8),
            DispatcherPriority.Render, (_, _) => RenderFrame());
        _timer.Start();

        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _timer?.Stop();
        _timer = null;
        _timerResToken?.Dispose();
        _timerResToken = null;
        _input?.Dispose();
        _input = null;
        if (_selMaskView is not null) { _gpu?.Api.TextureViewRelease(_selMaskView); _selMaskView = null; }
        if (_selMaskTex is not null) { _gpu?.Api.TextureRelease(_selMaskTex); _selMaskTex = null; }
        ReleaseSmartSelect();
        _compositor?.Dispose();
        _compositor = null;
        _blitter?.Dispose();
        _blitter = null;
        if (_surface is not null && _gpu is not null) { _gpu.Api.SurfaceRelease(_surface); _surface = null; }
        _gpu?.Dispose();
        _gpu = null;
        base.DestroyNativeControlCore(control);
    }

    private void InitGpu()
    {
        _gpu = new WgpuDevice();
        var api = _gpu.Api;

        // --- surface via the OS backend (Win32 HWND / Xlib / Metal) ---
        try
        {
            _surface = Platform.CanvasPlatform.Current.CreateSurface(_gpu, _hwnd);
        }
        catch (PlatformNotSupportedException ex)
        {
            // shared engine/UI still run; the GPU canvas just stays blank on this OS.
            System.Diagnostics.Debug.WriteLine($"[canvas] {ex.Message}");
            _surface = null;
            return;
        }

        // --- pick a supported format ---
        var caps = new SurfaceCapabilities();
        api.SurfaceGetCapabilities(_surface, _gpu.Adapter, ref caps);
        if (caps.FormatCount > 0 && caps.Formats is not null)
        {
            _format = caps.Formats[0];
            for (uint i = 0; i < caps.FormatCount; i++)
                if (caps.Formats[i] == TextureFormat.Bgra8Unorm) { _format = TextureFormat.Bgra8Unorm; break; }
        }

        _compositor = new GpuCompositor(_gpu);
        _blitter = new SurfaceBlitter(_gpu, _format);
        // no implicit demo — the canvas stays empty until MainWindow opens a document/tab.
    }

    private void Configure(uint w, uint h)
    {
        if (_gpu is null || _surface is null || w == 0 || h == 0) return;
        var config = new SurfaceConfiguration
        {
            Device = _gpu.Device,
            Format = _format,
            Usage = TextureUsage.RenderAttachment,
            Width = w,
            Height = h,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto
        };
        _gpu.Api.SurfaceConfigure(_surface, in config);
        _configured = true;
    }

    // loupe rim colour cache (the picked colour) keyed by integer doc pixel — avoids a per-frame
    // ReadComposite() readback while the eyedropper hovers stationary (esp. with all-layers sampling).
    private int _loupeColX = int.MinValue, _loupeColY = int.MinValue;
    private float _loupeColR, _loupeColG, _loupeColB;
    private bool _loupeColValid;

    private void UpdatePreviewDab()
    {
        if (_compositor is null) return;
        Sable.Engine.Compositing.PreviewDab? dab = null;
        bool previewTool = ActiveTool is Sable.Tools.ToolKind.Brush or Sable.Tools.ToolKind.Eraser
                                       or Sable.Tools.ToolKind.CloneStamp;
        if (!_painting && !QuickMask && !EyedropperSampling && ActiveLayer is { } al && previewTool)
        {
            var vp = ComputeViewport();
            double docX = vp.Scale > 0 ? (_lastMouseX - vp.Ox) / vp.Scale : -1;
            double docY = vp.Scale > 0 ? (_lastMouseY - vp.Oy) / vp.Scale : -1;
            if (docX >= 0 && docY >= 0 && docX < al.Width && docY < al.Height)
            {
                bool clone = ActiveTool == Sable.Tools.ToolKind.CloneStamp;
                // clone: preview the source content under the cursor (offset = cursor - source)
                if (clone && !_cloneSet) { _compositor.Preview = null; return; }   // no source yet
                dab = new Sable.Engine.Compositing.PreviewDab(al, (float)docX, (float)docY,
                    Brush.Radius, Brush.Hardness, Brush.R, Brush.G, Brush.B,
                    Erase: ActiveTool == Sable.Tools.ToolKind.Eraser,
                    IsClone: clone,
                    CloneOffX: clone ? (int)System.Math.Round(docX - _cloneSrcX) : 0,
                    CloneOffY: clone ? (int)System.Math.Round(docY - _cloneSrcY) : 0);
            }
        }
        _compositor.Preview = dab;
    }

    /// <summary>(Re)upload the document's selection coverage mask to an R8 texture for the edge overlay.</summary>
    private void UpdateSelMaskTexture()
    {
        if (_gpu is null || _doc?.SelectionMask is not { } mask) return;
        var api = _gpu.Api;
        int w = _doc.Width, h = _doc.Height;

        if (_selMaskTex is null || _selMaskTexW != w || _selMaskTexH != h)
        {
            if (_selMaskView is not null) { api.TextureViewRelease(_selMaskView); _selMaskView = null; }
            if (_selMaskTex is not null) { api.TextureRelease(_selMaskTex); _selMaskTex = null; }
            var td = new TextureDescriptor
            {
                Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D { Width = (uint)w, Height = (uint)h, DepthOrArrayLayers = 1 },
                Format = TextureFormat.R8Unorm, MipLevelCount = 1, SampleCount = 1
            };
            _selMaskTex = api.DeviceCreateTexture(_gpu.Device, in td);
            _selMaskView = api.TextureCreateView(_selMaskTex, null);
            _selMaskTexW = w; _selMaskTexH = h;
            _selMaskVer = -1;   // force upload of the new texture
        }

        if (_selMaskVer == _doc.SelectionVersion) return;   // already current

        // QueueWriteTexture requires bytesPerRow to be a 256-byte multiple → pad rows.
        int aligned = (w + 255) & ~255;
        byte[] src = mask;
        if (aligned != w)
        {
            var padded = new byte[aligned * h];
            for (int y = 0; y < h; y++) Array.Copy(mask, y * w, padded, y * aligned, w);
            src = padded;
        }
        var dst = new ImageCopyTexture { Texture = _selMaskTex, MipLevel = 0, Aspect = TextureAspect.All };
        var layout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)aligned, RowsPerImage = (uint)h };
        var ext = new Extent3D { Width = (uint)w, Height = (uint)h, DepthOrArrayLayers = 1 };
        fixed (byte* p = src)
            api.QueueWriteTexture(_gpu.Queue, in dst, p, (nuint)src.Length, in layout, in ext);
        _selMaskVer = _doc.SelectionVersion;
    }

    private void RenderFrame()
    {
        if (_gpu is null || _surface is null || _compositor is null || _blitter is null || _doc is null) return;

        var px = (uint)Math.Max(1, Bounds.Width);
        var py = (uint)Math.Max(1, Bounds.Height);
        if (!_configured || px != _width || py != _height)
        {
            _width = px; _height = py;
            Configure(px, py);
        }

        // live brush preview: composite a dab into the active layer's place in the stack
        UpdatePreviewDab();

        // recomposite only when the document changed OR the preview dab MOVED/changed.
        // A stationary hover (dab unchanged) reuses the last composite — avoids a
        // full-doc recomposite every frame just because the brush tool is active.
        var dab = _compositor.Preview;
        bool dabChanged = !Nullable.Equals(dab, _lastPreview);
        if (_compositeView is null || _doc.NeedsComposite || dabChanged)
        {
            // composite-cache hint: the edited layer → backdrop below it is reused (PLAN §7 hot-path)
            _compositor.CacheHintLayer = ActiveLayer;
            _compositeView = _compositor.Composite(_doc);
        }
        _lastPreview = dab;

        var api = _gpu.Api;
        SurfaceTexture st = default;
        api.SurfaceGetCurrentTexture(_surface, ref st);
        if (st.Status != SurfaceGetCurrentTextureStatus.Success)
        {
            // Surface went stale (e.g. the window was occluded by the file dialog or
            // resized). Reconfigure so we recover next frame instead of freezing on
            // old content until a manual resize forces a Configure.
            if (st.Status is SurfaceGetCurrentTextureStatus.Outdated
                          or SurfaceGetCurrentTextureStatus.Lost)
                Configure(_width, _height);
            if (st.Texture is not null) api.TextureRelease(st.Texture);
            return;
        }
        if (st.Texture is null) return;

        var view = api.TextureCreateView(st.Texture, null);

        var ov = default(BlitOverlay);
        if (_doc?.Selection is { } sel)   // active selection (any tool)
        {
            if (_doc.SelectionMask is not null)
            {
                // non-rect (ellipse/lasso/wand): trace ants along the true coverage edge
                UpdateSelMaskTexture();
                ov.MaskOn = _selMaskView is not null;
                ov.MaskView = _selMaskView;
                ov.QuickMask = QuickMask;   // rubylith fill instead of ants while editing
            }
            else
            {
                // plain rectangle: bounding-box ants + editable grips for the marquee
                ov.RectOn = true;
                ov.RectX = sel.X; ov.RectY = sel.Y; ov.RectW = sel.W; ov.RectH = sel.H;
                ov.SelHandles = ActiveTool == Sable.Tools.ToolKind.Marquee;
            }
        }
        if (ActiveTool == Sable.Tools.ToolKind.Crop && _cropRect is { } cr)
        {
            ov.CropOn = true;
            ov.RectX = cr.X; ov.RectY = cr.Y; ov.RectW = cr.W; ov.RectH = cr.H;
        }
        if (ActiveTool == Sable.Tools.ToolKind.CloneStamp && _cloneSet)
        {
            var vp = ComputeViewport();
            double srcDx, srcDy;
            if (_painting) { var (lx, ly) = MapToDoc(_lastMouseX, _lastMouseY); srcDx = lx - Brush.CloneOffX; srcDy = ly - Brush.CloneOffY; }
            else { srcDx = _cloneSrcX; srcDy = _cloneSrcY; }
            ov.CloneSrcOn = true;
            ov.CloneSrcSx = (float)(vp.Ox + srcDx * vp.Scale);
            ov.CloneSrcSy = (float)(vp.Oy + srcDy * vp.Scale);
        }
        if (EditingText is { } et)
        {
            var vp = ComputeViewport();
            double cxDoc = et.X + et.OffsetX + et.CaretX;       // end of the last line
            double y0Doc = et.Y + et.OffsetY + et.CaretY;
            double chDoc = et.CaretH > 0 ? et.CaretH : et.FontSize;
            ov.CaretOn = true;
            ov.CaretX = (float)(vp.Ox + cxDoc * vp.Scale);
            ov.CaretY0 = (float)(vp.Oy + y0Doc * vp.Scale);
            ov.CaretY1 = (float)(vp.Oy + (y0Doc + chDoc) * vp.Scale);
        }
        if (_shaping)
        {
            ov.ShapeOn = true;
            ov.ShapeKind = ActiveTool switch
            {
                Sable.Tools.ToolKind.ShapeEllipse => 1,
                Sable.Tools.ToolKind.ShapeLine or Sable.Tools.ToolKind.ShapeArrow => 2,
                _ => 0   // rect bbox preview for rect/rounded/polygon/star
            };
            ov.ShX0 = (float)_shapeStartSx; ov.ShY0 = (float)_shapeStartSy;
            ov.ShX1 = (float)_shapeEndSx; ov.ShY1 = (float)_shapeEndSy;
        }
        // Move: tight content bounds of the selected layer of ANY type (shape = the shape rect)
        if (ActiveTool == Sable.Tools.ToolKind.Move && !ov.RectOn && SelLayer is { } sl && _doc is { } md)
        {
            var (bx, by, bw, bh) = sl.ContentBounds(md.Width, md.Height);
            ov.RectOn = true;
            ov.RectX = bx + sl.OffsetX; ov.RectY = by + sl.OffsetY; ov.RectW = bw; ov.RectH = bh;
        }
        if (ActiveLayer is { } l)
        {
            if (ActiveTool == Sable.Tools.ToolKind.Transform)
            {
                ov.GizmoOn = true;
                ov.Corners = CornersSurface(l);
                ov.RotateHandleDist = RotHandleDist;
            }
            else if (!EyedropperSampling && ActiveTool is Sable.Tools.ToolKind.Brush or Sable.Tools.ToolKind.Eraser
                                or Sable.Tools.ToolKind.CloneStamp or Sable.Tools.ToolKind.Heal
                                or Sable.Tools.ToolKind.SpotHeal or Sable.Tools.ToolKind.Dodge
                                or Sable.Tools.ToolKind.Burn or Sable.Tools.ToolKind.Sponge
                                or Sable.Tools.ToolKind.BlurBrush or Sable.Tools.ToolKind.SharpenBrush
                                or Sable.Tools.ToolKind.Smudge or Sable.Tools.ToolKind.Liquify)
            {
                var vp = ComputeViewport();
                ov.BrushOn = true;
                ov.BrushX = (float)_lastMouseX;
                ov.BrushY = (float)_lastMouseY;
                ov.BrushR = Brush.Radius * vp.Scale;
                ov.BrushColR = Brush.R / 255f;
                ov.BrushColG = Brush.G / 255f;
                ov.BrushColB = Brush.B / 255f;
                ov.BrushErase = ActiveTool == Sable.Tools.ToolKind.Eraser;
                ov.BrushHardness = Brush.Hardness;
            }
            else if (ActiveTool == Sable.Tools.ToolKind.Gradient && _gradienting)
            {
                ov.GradientOn = true;
                ov.GradX0 = (float)_gradStartSx; ov.GradY0 = (float)_gradStartSy;
                ov.GradX1 = (float)_gradEndSx; ov.GradY1 = (float)_gradEndSy;
            }
        }
        // eyedropper loupe — circular magnifier of the pixels under the cursor (what gets sampled)
        if (EyedropperSampling && _doc is { } ed)
        {
            var (dgx, dgy) = MapToDoc(_lastMouseX, _lastMouseY);
            if (dgx >= 0 && dgy >= 0 && dgx < ed.Width && dgy < ed.Height)
            {
                const float r = 60f, zoom = 8f;   // centred on the cursor; magnify 8 surface px / doc px
                ov.LoupeOn = true;
                ov.LoupeCx = (float)_lastMouseX; ov.LoupeCy = (float)_lastMouseY; ov.LoupeR = r; ov.LoupeZoom = zoom;
                ov.LoupeDocX = (float)dgx; ov.LoupeDocY = (float)dgy;
                int idgx = (int)dgx, idgy = (int)dgy;
                if (idgx != _loupeColX || idgy != _loupeColY)
                {
                    _loupeColX = idgx; _loupeColY = idgy;
                    if (SampleColorValue(dgx, dgy) is { } pc0)
                    {
                        _loupeColR = pc0.r / 255f; _loupeColG = pc0.g / 255f; _loupeColB = pc0.b / 255f;
                        _loupeColValid = true;
                    }
                    else _loupeColValid = false;
                }
                if (_loupeColValid) { ov.LoupeColR = _loupeColR; ov.LoupeColG = _loupeColG; ov.LoupeColB = _loupeColB; }
            }
        }
        ov.PasteR = _pasteR; ov.PasteG = _pasteG; ov.PasteB = _pasteB;
        ov.GridOn = ShowGrid; ov.GridSpacing = GridSpacing; ov.PixelGrid = ShowPixelGrid;
        if (_overlayColorsSet)
        {
            ov.HasOverlayColors = true;
            ov.GuideColR = _guideR; ov.GuideColG = _guideG; ov.GuideColB = _guideB;
            ov.SmartColR = _smartR; ov.SmartColG = _smartG; ov.SmartColB = _smartB;
            ov.GridColR = _gridR; ov.GridColG = _gridG; ov.GridColB = _gridB;
            ov.QuickMaskColR = _qmR; ov.QuickMaskColG = _qmG; ov.QuickMaskColB = _qmB;
        }
        if (_doc is { } gd)
        {
            if (gd.GuidesX.Count > 0) ov.GuidesX = gd.GuidesX.ToArray();
            if (gd.GuidesY.Count > 0) ov.GuidesY = gd.GuidesY.ToArray();
        }
        if (_smartX.Count > 0) ov.SmartX = _smartX.ToArray();
        if (_smartY.Count > 0) ov.SmartY = _smartY.ToArray();
        if (PenActive) BuildPenOverlay(ref ov);
        else if (ActiveTool == Sable.Tools.ToolKind.Node && SelLayer is Sable.Engine.Layers.PathLayer np) BuildNodeOverlay(ref ov, np);
        else if (MeshActive) BuildMeshOverlay(ref ov);

        // AI hover-select preview: striped object highlight under the cursor
        if (ActiveTool == Sable.Tools.ToolKind.SmartSelect && _previewMode > 0f && _previewCov is not null)
        {
            UpdatePreviewTexture();
            ov.PreviewMaskView = _previewView;
            ov.PreviewMode = _previewMode;
        }

        _blitter.Blit(_compositeView, view, ComputeViewport(), ov);
        api.SurfacePresent(_surface);

        api.TextureViewRelease(view);
        api.TextureRelease(st.Texture);
    }
}

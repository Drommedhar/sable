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
/// Windows-only for now (HWND surface). Linux (Xlib/Wayland) + macOS (CAMetalLayer)
/// surface descriptors are the cross-platform follow-up.
/// </summary>
public sealed unsafe partial class GpuSurfaceControl : NativeControlHost
{
    private WgpuDevice? _gpu;
    private GpuCompositor? _compositor;
    private SurfaceBlitter? _blitter;
    private Document? _doc;
    private TextureView* _compositeView;

    private Surface* _surface;
    private TextureFormat _format = TextureFormat.Bgra8Unorm;
    private DispatcherTimer? _timer;
    private nint _hwnd;
    private uint _width = 1, _height = 1;
    private bool _configured;
    private Sable.Engine.Compositing.PreviewDab? _lastPreview;

    // selection-mask overlay texture (R8, doc-sized) for edge marching-ants
    private Texture* _selMaskTex;
    private TextureView* _selMaskView;
    private int _selMaskTexW, _selMaskTexH, _selMaskVer = -1;

    // viewport: _zoom = 1 means fit-to-window; pan in surface pixels
    private double _zoom = 1.0;
    private double _panX, _panY;
    private double _lastMouseX, _lastMouseY;   // surface px, tracked from WndProc

    /// <summary>Zoom about the surface center (keyboard).</summary>
    public void ZoomBy(double factor) => ZoomAt(factor, _width / 2.0, _height / 2.0);

    /// <summary>Zoom keeping the document point under (sx,sy) fixed (mouse wheel).</summary>
    public void ZoomAt(double factor, double sx, double sy)
    {
        var old = ComputeViewport();
        if (old.Scale <= 0) { _zoom = Math.Clamp(_zoom * factor, 0.05, 64.0); return; }

        double docX = (sx - old.Ox) / old.Scale;
        double docY = (sy - old.Oy) / old.Scale;

        _zoom = Math.Clamp(_zoom * factor, 0.05, 64.0);

        double dw = _doc?.Width ?? 1, dh = _doc?.Height ?? 1;
        float fit = Math.Min(_width / (float)dw, _height / (float)dh);
        double newScale = fit * _zoom;
        // solve pan so (docX,docY) maps back to (sx,sy)
        _panX = sx - docX * newScale - (_width - dw * newScale) / 2.0;
        _panY = sy - docY * newScale - (_height - dh * newScale) / 2.0;
    }

    public void PanBy(double dx, double dy)
    {
        _panX += dx;
        _panY += dy;
    }

    public void ResetView()
    {
        _zoom = 1.0; _panX = 0; _panY = 0;
    }

    // OS-specific canvas bits (surface creation, timer resolution) live behind the backend;
    // everything else in this control is shared across platforms (PLAN §2.1/§2.2).
    private IDisposable? _timerResToken;

    /// <summary>The document currently shown. Set to swap what the canvas renders.</summary>
    public Document? Document
    {
        get => _doc;
        set { _doc = value; _compositeView = null; }
    }

    /// <summary>Flatten the current document to RGBA8 on the GPU and read it back (for export).</summary>
    public byte[]? ReadComposite()
    {
        if (_compositor is null || _doc is null) return null;
        _compositor.Preview = null;   // never bake the live brush preview into an export
        return _compositor.CompositeToBytes(_doc);
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
        _doc ??= Document.CreateDemo();
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

    private void UpdatePreviewDab()
    {
        if (_compositor is null) return;
        Sable.Engine.Compositing.PreviewDab? dab = null;
        if (!_painting && ActiveLayer is { } al &&
            ActiveTool is Sable.Tools.ToolKind.Brush or Sable.Tools.ToolKind.Eraser)
        {
            var vp = ComputeViewport();
            double docX = vp.Scale > 0 ? (_lastMouseX - vp.Ox) / vp.Scale : -1;
            double docY = vp.Scale > 0 ? (_lastMouseY - vp.Oy) / vp.Scale : -1;
            if (docX >= 0 && docY >= 0 && docX < al.Width && docY < al.Height)
                dab = new Sable.Engine.Compositing.PreviewDab(al, (float)docX, (float)docY,
                    Brush.Radius, Brush.Hardness, Brush.R, Brush.G, Brush.B,
                    ActiveTool == Sable.Tools.ToolKind.Eraser);
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
            _compositeView = _compositor.Composite(_doc);
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
            }
            else
            {
                // plain rectangle: bounding-box ants + editable grips for the marquee
                ov.RectOn = true;
                ov.RectX = sel.X; ov.RectY = sel.Y; ov.RectW = sel.W; ov.RectH = sel.H;
                ov.SelHandles = ActiveTool == Sable.Tools.ToolKind.Marquee;
            }
        }
        if (ActiveLayer is { } l)
        {
            if (ActiveTool == Sable.Tools.ToolKind.Move && !ov.RectOn)
            {
                ov.RectOn = true;
                ov.RectX = l.OffsetX; ov.RectY = l.OffsetY; ov.RectW = l.Width; ov.RectH = l.Height;
            }
            else if (ActiveTool == Sable.Tools.ToolKind.Transform)
            {
                ov.GizmoOn = true;
                ov.Corners = CornersSurface(l);
                ov.RotateHandleDist = RotHandleDist;
            }
            else if (ActiveTool is Sable.Tools.ToolKind.Brush or Sable.Tools.ToolKind.Eraser)
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
        }
        _blitter.Blit(_compositeView, view, ComputeViewport(), ov);
        api.SurfacePresent(_surface);

        api.TextureViewRelease(view);
        api.TextureRelease(st.Texture);
    }
}

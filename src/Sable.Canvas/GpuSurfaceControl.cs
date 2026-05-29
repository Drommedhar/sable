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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    // Windows default timer granularity is ~15.6ms, so a 16ms DispatcherTimer
    // quantizes to ~31ms (~33fps). Raise resolution to 1ms while the canvas lives.
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
    private bool _timerPeriodSet;

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

        if (OperatingSystem.IsWindows())
        {
            InitGpu();
            HookInput();   // subclass the child HWND for mouse (airspace workaround)
            _timerPeriodSet = timeBeginPeriod(1) == 0;   // 1ms timer resolution
            // Render priority (not the default Background) so the present tick isn't
            // starved behind input/layout — that was pinning the canvas at ~30fps.
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8),
                DispatcherPriority.Render, (_, _) => RenderFrame());
            _timer.Start();
        }

        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _timer?.Stop();
        _timer = null;
        if (_timerPeriodSet) { timeEndPeriod(1); _timerPeriodSet = false; }
        UnhookInput();
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

        // --- surface from Win32 HWND ---
        var hinstance = GetModuleHandleW(null);
        var fromHwnd = new SurfaceDescriptorFromWindowsHWND
        {
            Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromWindowsHwnd },
            Hinstance = (void*)hinstance,
            Hwnd = (void*)_hwnd
        };
        var surfDesc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromHwnd };
        _surface = api.InstanceCreateSurface(_gpu.Instance, in surfDesc);
        if (_surface is null) throw new InvalidOperationException("wgpu: surface creation from HWND failed.");

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
        if (_doc?.Selection is { } sel)   // active selection marching ants (any tool)
        {
            ov.RectOn = true;
            ov.RectX = sel.X; ov.RectY = sel.Y; ov.RectW = sel.W; ov.RectH = sel.H;
            // grips only for an editable rectangular marquee (not mask selections)
            ov.SelHandles = ActiveTool == Sable.Tools.ToolKind.Marquee && _doc.SelectionMask is null;
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

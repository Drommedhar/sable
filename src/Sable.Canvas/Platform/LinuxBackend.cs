using System.Runtime.InteropServices;
using Sable.Gpu;
using Silk.NET.WebGPU;

namespace Sable.Canvas.Platform;

/// <summary>
/// Linux canvas backend: Avalonia's X11 window (an XID) → wgpu Xlib surface (PLAN §2.2).
/// Avalonia's desktop backend on Linux is X11, so even on a Wayland session the canvas
/// is hosted in an X11 window (via XWayland) and <c>NativeControlHost</c> hands us an XID,
/// NOT a <c>wl_surface</c>. wgpu's Vulkan backend wraps it through
/// <see cref="SurfaceDescriptorFromXlibWindow"/>; XWayland presents it. Everything past this
/// seam — compositor, viewport, tools — is identical to Windows/macOS.
///
/// The surface uses its OWN X11 <c>Display</c> connection (kept for the process lifetime),
/// separate from the input source's connection, so wgpu's WSI and our X11 event pump never
/// touch the same connection from different threads (Xlib is not thread-safe per-connection).
/// </summary>
internal sealed unsafe class LinuxBackend : IPlatformBackend
{
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);

    private nint _display;   // dedicated connection for the wgpu surface

    public Surface* CreateSurface(WgpuDevice gpu, nint windowHandle)
    {
        if (windowHandle == 0)
            throw new InvalidOperationException("Linux/X11: null window handle for the canvas surface.");

        if (_display == 0) _display = XOpenDisplay(0);
        if (_display == 0)
            throw new PlatformNotSupportedException(
                "Linux/X11: XOpenDisplay failed (no X server / DISPLAY). The canvas stays blank.");

        var fromXlib = new SurfaceDescriptorFromXlibWindow
        {
            Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromXlibWindow },
            Display = (void*)_display,
            Window = (ulong)windowHandle
        };
        var surfDesc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromXlib };
        var surface = gpu.Api.InstanceCreateSurface(gpu.Instance, in surfDesc);
        if (surface is null) throw new InvalidOperationException("wgpu: surface creation from Xlib window failed.");
        return surface;
    }

    public IInputSource CreateInput() => new X11InputSource();

    // No Windows-style 15.6ms timer quantization on Linux; the dispatcher render tick runs at
    // full resolution already.
    public IDisposable RaiseTimerResolution() => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

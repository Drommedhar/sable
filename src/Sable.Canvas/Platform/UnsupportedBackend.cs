using Sable.Gpu;
using Silk.NET.WebGPU;

namespace Sable.Canvas.Platform;

/// <summary>
/// Stub backend for OSes whose canvas surface/input aren't wired yet (Linux Xlib/Wayland,
/// macOS CAMetalLayer — PLAN §15). The shared engine/UI still run; the GPU canvas stays
/// blank rather than crashing. Implementing a new OS = replace this with a real backend:
/// build the matching wgpu surface descriptor and a native input source.
/// </summary>
internal sealed unsafe class UnsupportedBackend(string os) : IPlatformBackend
{
    public Surface* CreateSurface(WgpuDevice gpu, nint windowHandle)
        => throw new PlatformNotSupportedException(
            $"Sable canvas surface not yet implemented for {os}. " +
            "wgpu supports it (Vulkan/Metal); the surface descriptor + native input source are the TODO (PLAN §15).");

    // No native input wired yet on this OS; the canvas takes no pointer input.
    public IInputSource CreateInput() => new NullInputSource();

    // No multimedia timer outside Windows; the render tick runs at the dispatcher's resolution.
    public IDisposable RaiseTimerResolution() => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class NullInputSource : IInputSource
    {
        public void Attach(nint windowHandle, ICanvasInputSink sink) { }
        public void Capture() { }
        public void ReleaseCapture() { }
        public void Dispose() { }
    }
}

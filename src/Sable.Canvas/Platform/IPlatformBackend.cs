using Sable.Gpu;
using Silk.NET.WebGPU;

namespace Sable.Canvas.Platform;

/// <summary>
/// The one seam for OS-specific canvas code (PLAN §2.1/§2.2). Everything else —
/// render loop, compositor, viewport, tool logic, coordinate mapping — is shared
/// across platforms. Only the irreducible per-OS bits live behind this interface:
/// turning a native window handle into a wgpu surface, the native input source,
/// and the timer-resolution tweak. wgpu itself is already cross-platform
/// (DX12 / Vulkan / Metal); a new OS = one new <see cref="IPlatformBackend"/>.
/// </summary>
public unsafe interface IPlatformBackend
{
    /// <summary>
    /// Create a wgpu <see cref="Surface"/> for the native window handle from Avalonia's
    /// <c>NativeControlHost</c>. Throws <see cref="PlatformNotSupportedException"/> where
    /// not yet implemented.
    /// </summary>
    Surface* CreateSurface(WgpuDevice gpu, nint windowHandle);

    /// <summary>
    /// Native input source for the canvas child window. Decodes OS mouse/key events into
    /// shared <see cref="ICanvasInputSink"/> callbacks; the tool logic consuming them is
    /// platform-agnostic.
    /// </summary>
    IInputSource CreateInput();

    /// <summary>
    /// Raise OS timer resolution so the render tick isn't quantized (Windows: 15.6ms →
    /// 1ms). Returns a token; dispose to restore. No-op where not applicable.
    /// </summary>
    IDisposable RaiseTimerResolution();
}

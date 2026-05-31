using Sable.Gpu;
using Silk.NET.WebGPU;

namespace Sable.Canvas.Platform;

/// <summary>
/// macOS canvas backend: Avalonia NSView → wgpu Metal surface (PLAN §2.2). The view from
/// Avalonia's <c>NativeControlHost</c> is made layer-hosting with a freshly-created
/// <c>CAMetalLayer</c>, which wgpu wraps via <see cref="SurfaceDescriptorFromMetalLayer"/>.
/// The AppKit-managed host layer tracks the view bounds, so a resize just reaches
/// <c>SurfaceConfigure</c> in the shared render loop (which sets the layer's drawableSize).
/// Everything past this seam — compositor, viewport, tools — is identical to Windows.
/// </summary>
internal sealed unsafe class MacBackend : IPlatformBackend
{
    public Surface* CreateSurface(WgpuDevice gpu, nint windowHandle)
    {
        nint view = windowHandle;   // NSView* from NativeControlHost.CreateNativeControlCore
        if (view == 0) throw new InvalidOperationException("macOS: null NSView handle for the canvas surface.");

        // Build a CAMetalLayer and host it in the view. [CAMetalLayer layer] is autoreleased, so
        // retain it for the lifetime of the surface (wgpu keeps a reference to this layer).
        nint metalLayer = ObjC.SendPtr(ObjC.Cls("CAMetalLayer"), ObjC.Sel("layer"));
        if (metalLayer == 0) throw new InvalidOperationException("macOS: failed to create CAMetalLayer.");
        metalLayer = ObjC.SendPtr(metalLayer, ObjC.Sel("retain"));

        // 1pt = 1px so the swapchain (configured from the control's DIP Bounds in the shared render
        // loop) matches the layer's drawable. Retina-crisp 1:1 scaling is the same HiDPI follow-up
        // tracked on the Windows path, not a per-OS concern.
        ObjC.SendVoidDouble(metalLayer, ObjC.Sel("setContentsScale:"), 1.0);

        // Order matters: assign the layer, THEN opt into layering → layer-hosting view.
        ObjC.SendVoidPtr(view, ObjC.Sel("setLayer:"), metalLayer);
        ObjC.SendVoidBool(view, ObjC.Sel("setWantsLayer:"), true);

        var fromMetal = new SurfaceDescriptorFromMetalLayer
        {
            Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromMetalLayer },
            Layer = (void*)metalLayer
        };
        var surfDesc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromMetal };
        var surface = gpu.Api.InstanceCreateSurface(gpu.Instance, in surfDesc);
        if (surface is null) throw new InvalidOperationException("wgpu: surface creation from CAMetalLayer failed.");
        return surface;
    }

    public IInputSource CreateInput() => new MacInputSource();

    // macOS has no Windows-style 15.6ms timer quantization; the dispatcher render tick runs at full
    // resolution already. No multimedia-timer equivalent is needed.
    public IDisposable RaiseTimerResolution() => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

using System.Runtime.InteropServices;
using Sable.Gpu;
using Silk.NET.WebGPU;

namespace Sable.Canvas.Platform;

/// <summary>Windows canvas backend: Win32 HWND → wgpu surface, multimedia timer resolution.</summary>
internal sealed unsafe class WindowsBackend : IPlatformBackend
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    public Surface* CreateSurface(WgpuDevice gpu, nint windowHandle)
    {
        var hinstance = GetModuleHandleW(null);
        var fromHwnd = new SurfaceDescriptorFromWindowsHWND
        {
            Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromWindowsHwnd },
            Hinstance = (void*)hinstance,
            Hwnd = (void*)windowHandle
        };
        var surfDesc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromHwnd };
        var surface = gpu.Api.InstanceCreateSurface(gpu.Instance, in surfDesc);
        if (surface is null) throw new InvalidOperationException("wgpu: surface creation from HWND failed.");
        return surface;
    }

    public IInputSource CreateInput() => new WindowsInputSource();

    public IDisposable RaiseTimerResolution()
    {
        // Default Windows timer granularity ~15.6ms quantizes a 16ms render tick to ~31ms (~33fps).
        bool ok = timeBeginPeriod(1) == 0;
        return new Restore(ok);
    }

    private sealed class Restore(bool active) : IDisposable
    {
        public void Dispose() { if (active) { timeEndPeriod(1); active = false; } }
    }
}

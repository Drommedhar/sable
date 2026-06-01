using System.Runtime.InteropServices;

namespace Sable.Ai.Gpu;

/// <summary>
/// General (vendor-agnostic) total-VRAM probe for Windows via DXGI. Reads each adapter's
/// <c>DedicatedVideoMemory</c> (DXGI_ADAPTER_DESC1) and returns the largest — picks the discrete GPU
/// on a laptop with an integrated + discrete pair, and works for AMD/Intel/NVIDIA alike (unlike
/// nvidia-smi). Returns 0 off Windows or on any failure. Pure native interop via the always-present
/// dxgi.dll — no extra NuGet package. Used to pick a sensible default for VRAM-scaled work.
/// </summary>
internal static unsafe class DxgiVram
{
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint ppFactory);

    // IID_IDXGIFactory1 = {770aae78-f26f-4dba-a829-253c83d1b387}
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;                       // bit 0x2 = DXGI_ADAPTER_FLAG_SOFTWARE (WARP)
    }

    /// <summary>Largest discrete adapter's dedicated VRAM in bytes; 0 if unknown / not Windows.</summary>
    public static ulong LargestDedicatedBytes()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return Probe(); }
        catch { return 0; }
    }

    private static ulong Probe()
    {
        if (CreateDXGIFactory1(in IID_IDXGIFactory1, out nint factory) != 0 || factory == 0) return 0;
        try
        {
            ulong max = 0;
            var fvt = *(void***)factory;
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12 (IUnknown 0-2, IDXGIObject 3-6, IDXGIFactory 7-11).
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)fvt[12];
            for (uint i = 0; ; i++)
            {
                nint adapter;
                int hr = enumAdapters1(factory, i, &adapter);
                if (hr != 0 || adapter == 0) break;   // DXGI_ERROR_NOT_FOUND ends enumeration
                try
                {
                    var avt = *(void***)adapter;
                    // IDXGIAdapter1::GetDesc1 is vtable slot 10 (IDXGIAdapter 7-9 + GetDesc1).
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)avt[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) == 0 && (desc.Flags & 0x2) == 0)
                    {
                        ulong v = desc.DedicatedVideoMemory;
                        if (v > max) max = v;
                    }
                }
                finally { Release(adapter); }
            }
            return max;
        }
        finally { Release(factory); }
    }

    private static void Release(nint p)
    {
        var vt = *(void***)p;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vt[2])(p);   // IUnknown::Release
    }
}

using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;

namespace Sable.Gpu;

/// <summary>
/// Minimal wgpu (WebGPU) device bootstrap: instance -> adapter -> device -> queue.
/// wgpu-native resolves adapter/device synchronously (callbacks fire during the request call),
/// so no async polling is needed here.
/// </summary>
public sealed unsafe class WgpuDevice : IDisposable
{
    public WebGPU Api { get; }
    private readonly Wgpu _wgpu;
    public Instance* Instance { get; }
    public Adapter* Adapter { get; }
    public Device* Device { get; }
    public Queue* Queue { get; }

    /// <summary>Max bytes bindable from a single storage buffer (caps the tile-atlas size). Default
    /// WebGPU limit is 128 MiB; we request the adapter's higher value when available.</summary>
    public ulong MaxStorageBinding { get; private set; } = 128u * 1024 * 1024;
    /// <summary>Max single-buffer allocation the device permits (also caps the atlas).</summary>
    public ulong MaxBufferSize { get; private set; } = 256u * 1024 * 1024;

    public WgpuDevice()
    {
        Api = WebGPU.GetApi();
        _wgpu = new Wgpu(Api.Context);

        var instanceDesc = new InstanceDescriptor();
        Instance = Api.CreateInstance(in instanceDesc);
        if (Instance is null)
            throw new InvalidOperationException("wgpu: CreateInstance returned null.");

        // --- request adapter (sync under wgpu-native) ---
        Adapter* adapter = null;
        var adapterOpts = new RequestAdapterOptions { PowerPreference = PowerPreference.HighPerformance };
        var adapterCb = PfnRequestAdapterCallback.From((status, a, msgPtr, _) =>
        {
            if (status == RequestAdapterStatus.Success) adapter = a;
            else throw new InvalidOperationException($"wgpu: adapter request failed: {SilkMarshal.PtrToString((nint)msgPtr)}");
        });
        Api.InstanceRequestAdapter(Instance, in adapterOpts, adapterCb, null);
        if (adapter is null)
            throw new InvalidOperationException("wgpu: no compatible GPU adapter found.");
        Adapter = adapter;

        // --- request device (sync under wgpu-native) ---
        Device* device = null;
        // non-throwing: leave device null on failure so the full-limits → defaults fallback can retry
        var deviceCb = PfnRequestDeviceCallback.From((status, d, msgPtr, _) =>
        {
            if (status == RequestDeviceStatus.Success) device = d;
        });
        // Request the adapter's full limits so the tile atlas can exceed the 128 MiB default
        // storage-buffer binding cap (PLAN §3/§17.3). Falls back to defaults if that request fails.
        var supported = new SupportedLimits();
        bool gotLimits = Api.AdapterGetLimits(Adapter, ref supported);
        if (gotLimits)
        {
            var required = new RequiredLimits { Limits = supported.Limits };
            var deviceDesc = new DeviceDescriptor { RequiredLimits = &required };
            Api.AdapterRequestDevice(Adapter, in deviceDesc, deviceCb, null);
        }
        if (device is null)
        {
            // retry with defaults (some drivers reject the full-limits request)
            var deviceDesc = new DeviceDescriptor();
            Api.AdapterRequestDevice(Adapter, in deviceDesc, deviceCb, null);
        }
        if (device is null)
            throw new InvalidOperationException("wgpu: device creation failed.");
        Device = device;

        // report the ACTUAL device limits (the raise above is best-effort and may have fallen back
        // to defaults) so the tile atlas never sizes past what this device can bind.
        var actual = new SupportedLimits();
        if (Api.DeviceGetLimits(Device, ref actual))
        {
            if (actual.Limits.MaxStorageBufferBindingSize > 0) MaxStorageBinding = actual.Limits.MaxStorageBufferBindingSize;
            if (actual.Limits.MaxBufferSize > 0) MaxBufferSize = actual.Limits.MaxBufferSize;
        }

        Queue = Api.DeviceGetQueue(Device);
    }

    /// <summary>Human-readable adapter description (vendor / backend).</summary>
    public string DescribeAdapter()
    {
        var props = new AdapterProperties();
        Api.AdapterGetProperties(Adapter, ref props);
        var name = SilkMarshal.PtrToString((nint)props.Name) ?? "unknown";
        return $"{name} [backend={props.BackendType}, type={props.AdapterType}]";
    }

    /// <summary>Pump the wgpu-native device queue until pending work (e.g. buffer maps) completes.</summary>
    public void Poll(bool wait = true) => _wgpu.DevicePoll(Device, wait, null);

    /// <summary>Compile a WGSL shader embedded in Sable.Gpu (Shaders/&lt;name&gt;.wgsl).</summary>
    public ShaderModule* CreateWgslModule(string name)
    {
        var code = (byte*)SilkMarshal.StringToPtr(ShaderLibrary.Load(name));
        var wgslDesc = new ShaderModuleWGSLDescriptor
        {
            Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
            Code = code
        };
        var smDesc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDesc };
        var module = Api.DeviceCreateShaderModule(Device, in smDesc);
        SilkMarshal.Free((nint)code);
        if (module is null) throw new InvalidOperationException($"wgpu: shader '{name}' compile failed.");
        return module;
    }

    public void Dispose()
    {
        if (Device is not null) Api.DeviceRelease(Device);
        if (Adapter is not null) Api.AdapterRelease(Adapter);
        if (Instance is not null) Api.InstanceRelease(Instance);
        Api.Dispose();
    }
}

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
        var deviceCb = PfnRequestDeviceCallback.From((status, d, msgPtr, _) =>
        {
            if (status == RequestDeviceStatus.Success) device = d;
            else throw new InvalidOperationException($"wgpu: device request failed: {SilkMarshal.PtrToString((nint)msgPtr)}");
        });
        var deviceDesc = new DeviceDescriptor();
        Api.AdapterRequestDevice(Adapter, in deviceDesc, deviceCb, null);
        if (device is null)
            throw new InvalidOperationException("wgpu: device creation failed.");
        Device = device;

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

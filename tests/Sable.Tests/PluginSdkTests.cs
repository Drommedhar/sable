using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Automation;
using Sable.Plugin.Sdk.Capabilities;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Document;
using Sable.Plugin.Sdk.Export;
using Sable.Plugin.Sdk.Host;
using Sable.Plugin.Sdk.Layers;
using Sable.Plugin.Sdk.Manifest;
using Sable.Plugin.Sdk.Permissions;
using Sable.Plugin.Sdk.Ui;
using Sable.Plugins;

namespace Sable.Tests;

public class PluginSdkTests
{
    // ---- SDK version negotiation ----

    [Theory]
    [InlineData("1", 1, true)]
    [InlineData("1.0", 1, true)]
    [InlineData(" 1 ", 1, true)]
    [InlineData("2.3", 2, true)]
    [InlineData("abc", 0, false)]
    [InlineData("", 0, false)]
    public void SdkVersion_ParsesMajor(string text, int expected, bool ok)
    {
        Assert.Equal(ok, SdkVersion.TryParseMajor(text, out var major));
        if (ok) Assert.Equal(expected, major);
    }

    [Fact]
    public void SdkVersion_CompatibilityIsExactMajorForP0()
    {
        Assert.True(SdkVersion.IsCompatible(SdkVersion.Current));
        Assert.False(SdkVersion.IsCompatible(SdkVersion.Current + 1));
        Assert.False(SdkVersion.IsCompatible(0));
    }

    // ---- Capabilities + permissions ----

    [Fact]
    public void Capability_KnownSetContainsP0()
    {
        Assert.True(Capability.IsKnown(Capability.DocumentRead));
        Assert.True(Capability.IsKnown(Capability.ExportProvider));
        Assert.True(Capability.Implemented.Contains(Capability.LayerWriteBasic));
        Assert.False(Capability.IsKnown("totally.made.up"));
    }

    [Theory]
    [InlineData("none", PermissionScope.None)]
    [InlineData("scoped", PermissionScope.Scoped)]
    [InlineData("full", PermissionScope.Full)]
    [InlineData("false", PermissionScope.None)]
    [InlineData("true", PermissionScope.Full)]
    public void Permissions_ParseScope(string text, PermissionScope expected)
    {
        Assert.True(PluginPermissions.TryParseScope(text, out var scope));
        Assert.Equal(expected, scope);
    }

    // ---- Manifest parsing/validation ----

    private const string ValidManifest = """
    {
      "id": "com.example.myplugin",
      "name": "My Plugin",
      "version": "0.1.0",
      "sdk_version": "1",
      "entrypoint": "Example.MyPlugin",
      "capabilities": ["document.read", "layer.write.basic", "export.provider"],
      "permissions": {
        "filesystem_read": "scoped",
        "filesystem_write": "scoped",
        "network": false,
        "gpu": false
      },
      "author": "Example",
      "website": "https://example.com"
    }
    """;

    [Fact]
    public void Manifest_ValidParsesAllFields()
    {
        var r = ManifestParser.Parse(ValidManifest);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var m = r.Manifest!;
        Assert.Equal("com.example.myplugin", m.Id);
        Assert.Equal("My Plugin", m.Name);
        Assert.Equal(1, m.SdkMajor);
        Assert.Equal("Example.MyPlugin", m.Entrypoint);
        Assert.Equal(3, m.Capabilities.Count);
        Assert.True(m.HasCapability(Capability.ExportProvider));
        Assert.Equal(PermissionScope.Scoped, m.Permissions.FilesystemRead);
        Assert.False(m.Permissions.Network);
        Assert.Equal("Example", m.Author);
    }

    [Fact]
    public void Manifest_MissingRequiredFieldsCollectsAllErrors()
    {
        var r = ManifestParser.Parse("""{ "name": "x" }""");
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("id"));
        Assert.Contains(r.Errors, e => e.Contains("version"));
        Assert.Contains(r.Errors, e => e.Contains("sdk_version"));
        Assert.Contains(r.Errors, e => e.Contains("entrypoint"));
        Assert.Contains(r.Errors, e => e.Contains("capabilities"));
    }

    [Fact]
    public void Manifest_UnknownCapabilityRejected()
    {
        var json = ValidManifest.Replace("\"export.provider\"", "\"fly.to.the.moon\"");
        var r = ManifestParser.Parse(json);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("unknown capability") && e.Contains("fly.to.the.moon"));
    }

    [Fact]
    public void Manifest_IncompatibleSdkVersionRejected()
    {
        var json = ValidManifest.Replace("\"sdk_version\": \"1\"", "\"sdk_version\": \"2\"");
        var r = ManifestParser.Parse(json);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("incompatible"));
    }

    [Fact]
    public void Manifest_MalformedJsonRejected()
    {
        var r = ManifestParser.Parse("{ not json ");
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("not valid JSON"));
    }

    [Fact]
    public void Manifest_DuplicateCapabilityRejected()
    {
        var json = ValidManifest.Replace(
            "[\"document.read\", \"layer.write.basic\", \"export.provider\"]",
            "[\"document.read\", \"document.read\"]");
        var r = ManifestParser.Parse(json);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("duplicate capability"));
    }

    [Fact]
    public void Manifest_NonDnsIdRejected()
    {
        var json = ValidManifest.Replace("\"com.example.myplugin\"", "\"myplugin\"");
        var r = ManifestParser.Parse(json);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("reverse-DNS"));
    }

    // ---- Discovery + loader ----

    private static string WritePluginDir(string root, string name, string? manifestJson, bool withDll = false)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        if (manifestJson is not null)
            File.WriteAllText(Path.Combine(dir, PluginDiscovery.ManifestFileName), manifestJson);
        if (withDll)
            File.WriteAllText(Path.Combine(dir, "fake.dll"), "not a real assembly");
        return dir;
    }

    [Fact]
    public void Discovery_FindsManifestDirsOnly()
    {
        var root = NewTempRoot();
        WritePluginDir(root, "p1", ValidManifest);
        WritePluginDir(root, "p2", null);            // no manifest -> skipped
        Directory.CreateDirectory(Path.Combine(root, "p1", "nested")); // sub-dir ignored

        var found = PluginDiscovery.Discover(root);
        Assert.Single(found);
        Assert.EndsWith("p1", found[0].Directory);
    }

    [Fact]
    public void Discovery_MissingRootIsEmpty()
        => Assert.Empty(PluginDiscovery.Discover(Path.Combine(NewTempRoot(), "does-not-exist")));

    [Fact]
    public void Loader_ValidatesGoodManifestAndRejectsBad()
    {
        var root = NewTempRoot();
        var goodDir = WritePluginDir(root, "good", ValidManifest);
        var badDir = WritePluginDir(root, "bad", """{ "id": "no.caps", "name": "x", "version": "1", "sdk_version": "1", "entrypoint": "X", "capabilities": [] }""");

        var loader = new PluginLoader(new CollectingLogger());

        var good = new LoadedPlugin(goodDir, Path.Combine(goodDir, "manifest.json"));
        Assert.True(loader.ValidateManifest(good));
        Assert.NotNull(good.Manifest);

        var bad = new LoadedPlugin(badDir, Path.Combine(badDir, "manifest.json"));
        Assert.False(loader.ValidateManifest(bad));
        Assert.Equal(PluginState.Failed, bad.State);
        Assert.NotEmpty(bad.Errors);
    }

    [Fact]
    public void Loader_AttachAndActivateGoodPlugin()
    {
        var root = NewTempRoot();
        var dir = WritePluginDir(root, "good", ValidManifest);
        var loader = new PluginLoader(new CollectingLogger());
        var plugin = new LoadedPlugin(dir, Path.Combine(dir, "manifest.json"));
        loader.ValidateManifest(plugin);

        var instance = new GoodPlugin();
        Assert.True(loader.AttachInstance(plugin, instance));
        Assert.Equal(PluginState.Loaded, plugin.State);

        Assert.True(loader.Activate(plugin, new FakeHost(plugin.Manifest!)));
        Assert.Equal(PluginState.Active, plugin.State);
        Assert.True(instance.Initialized);

        loader.Deactivate(plugin);
        Assert.Equal(PluginState.Loaded, plugin.State);
        Assert.True(instance.ShutDown);
    }

    [Fact]
    public void Loader_ThrowingPluginIsIsolatedAndQuarantinedAfterThreshold()
    {
        var root = NewTempRoot();
        var dir = WritePluginDir(root, "bad", ValidManifest);
        var loader = new PluginLoader(new CollectingLogger());
        var plugin = new LoadedPlugin(dir, Path.Combine(dir, "manifest.json"));
        loader.ValidateManifest(plugin);
        loader.AttachInstance(plugin, new ThrowingPlugin());

        for (int i = 0; i < PluginRegistry.CrashThreshold; i++)
            Assert.False(loader.Activate(plugin, new FakeHost(plugin.Manifest!)));

        Assert.Equal(PluginRegistry.CrashThreshold, plugin.CrashCount);
        Assert.Equal(PluginState.Quarantined, plugin.State);
    }

    [Fact]
    public void Loader_NoAssemblyFailsCleanly()
    {
        var root = NewTempRoot();
        var dir = WritePluginDir(root, "p", ValidManifest); // no dll
        var loader = new PluginLoader(new CollectingLogger());
        var plugin = new LoadedPlugin(dir, Path.Combine(dir, "manifest.json"));
        loader.ValidateManifest(plugin);

        Assert.False(loader.Load(plugin));
        Assert.Equal(PluginState.Failed, plugin.State);
        Assert.Contains(plugin.Errors, e => e.Contains("no assembly"));
    }

    // ---- Registry ----

    [Fact]
    public void Registry_AddDuplicateThrows()
    {
        var reg = new PluginRegistry();
        var a = MakeValidated();
        reg.Add(a);
        Assert.Throws<InvalidOperationException>(() => reg.Add(MakeValidated()));
        Assert.Single(reg.All);
        Assert.NotNull(reg.Get("com.example.myplugin"));
    }

    [Fact]
    public void Registry_DisableEnableTransitions()
    {
        var reg = new PluginRegistry();
        var p = MakeValidated(); // validated -> Discovered
        reg.Add(p);

        reg.Disable(p.Id);
        Assert.Equal(PluginState.Disabled, p.State);

        reg.Enable(p.Id);
        // No instance attached -> falls back to Discovered.
        Assert.Equal(PluginState.Discovered, p.State);
    }

    // ---- Manager ----

    [Fact]
    public void Manager_SafeModeDiscoversButDoesNotLoad()
    {
        var root = NewTempRoot();
        WritePluginDir(root, "good", ValidManifest);
        var mgr = new PluginManager(root, new CollectingLogger(), p => new FakeHost(p.Manifest!)) { SafeMode = true };

        var activated = mgr.LoadAll();
        Assert.Equal(0, activated);
        Assert.Single(mgr.Registry.All);
        Assert.Equal(PluginState.Discovered, mgr.Registry.All[0].State);
        Assert.NotNull(mgr.Registry.All[0].Manifest);
    }

    [Fact]
    public void Manager_LoadAllMarksDllLessPluginFailed()
    {
        var root = NewTempRoot();
        WritePluginDir(root, "good", ValidManifest); // valid manifest, no dll
        var mgr = new PluginManager(root, new CollectingLogger(), p => new FakeHost(p.Manifest!));

        var activated = mgr.LoadAll();
        Assert.Equal(0, activated);
        Assert.Equal(PluginState.Failed, mgr.Registry.All[0].State);
    }

    [Fact]
    public void Manager_AddBuiltInActivates()
    {
        var root = NewTempRoot();
        var dir = WritePluginDir(root, "good", ValidManifest);
        var plugin = new LoadedPlugin(dir, Path.Combine(dir, "manifest.json"));
        new PluginLoader(new CollectingLogger()).ValidateManifest(plugin);

        var mgr = new PluginManager(root, new CollectingLogger(), p => new FakeHost(p.Manifest!));
        var instance = new GoodPlugin();
        Assert.True(mgr.AddBuiltIn(plugin, instance));
        Assert.Equal(PluginState.Active, plugin.State);
        Assert.True(instance.Initialized);

        mgr.ShutdownAll();
        Assert.True(instance.ShutDown);
    }

    // ---- helpers ----

    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sable-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LoadedPlugin MakeValidated()
    {
        var root = NewTempRoot();
        var dir = Path.Combine(root, "p");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path, ValidManifest);
        var p = new LoadedPlugin(dir, path);
        new PluginLoader(new CollectingLogger()).ValidateManifest(p);
        return p;
    }

    private sealed class GoodPlugin : IPlugin
    {
        public bool Initialized;
        public bool ShutDown;
        public void Initialize(IHostContext host) => Initialized = true;
        public void Shutdown() => ShutDown = true;
    }

    private sealed class ThrowingPlugin : IPlugin
    {
        public void Initialize(IHostContext host) => throw new InvalidOperationException("boom");
        public void Shutdown() { }
    }

    private sealed class CollectingLogger : IPluginLogger
    {
        public readonly List<string> Messages = new();
        public void Log(LogLevel level, string message, Exception? error = null) => Messages.Add($"{level}:{message}");
    }

    private sealed class FakeHost : IHostContext
    {
        public FakeHost(PluginManifest manifest) => Manifest = manifest;
        public PluginManifest Manifest { get; }
        public IPluginLogger Logger { get; } = new CollectingLogger();
        public IPluginSettings Settings { get; } = new FakeSettings();
        public bool Has(string capability) => Manifest.HasCapability(capability);
        public IDocumentApi? Document => null;
        public ILayerApi? Layers => null;
        public ILayerWriteApi? LayerWrites => null;
        public ICommandApi? Commands => null;
        public IMenuApi? Menus => null;
        public IExportApi? Export => null;
        public Sable.Plugin.Sdk.Import.IImportApi? Import => null;
        public Sable.Plugin.Sdk.Selection.ISelectionApi? Selection => null;
        public Sable.Plugin.Sdk.Pixels.IPixelApi? Pixels => null;
        public ITransactionApi? Transactions => null;
    }

    private sealed class FakeSettings : IPluginSettings
    {
        private readonly Dictionary<string, string?> _d = new();
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string? value) => _d[key] = value;
        public bool Contains(string key) => _d.ContainsKey(key);
        public void Remove(string key) => _d.Remove(key);
        public void Save() { }
    }
}

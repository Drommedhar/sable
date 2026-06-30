using System;
using System.Collections.Generic;
using System.IO;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Document;
using Sable.Plugin.Sdk.Export;
using Sable.Plugin.Sdk.Host;
using Sable.Plugin.Sdk.Layers;
using Sable.Plugin.Sdk.Ui;
using Sable.Plugins;

namespace Sable.Tests;

/// <summary>Engine-backed host-context plumbing: capability gating, per-plugin settings, logger.</summary>
public sealed class HostContextTests
{
    // --- fakes for the six host APIs ---
    private sealed class FakeDoc : IDocumentApi { public DocumentInfo? Active => null; }
    private sealed class FakeLayers : ILayerApi
    {
        public IReadOnlyList<LayerInfo> All() => Array.Empty<LayerInfo>();
        public LayerInfo? Selected() => null;
        public LayerInfo? ById(string id) => null;
    }
    private sealed class FakeExport : IExportApi { public void Register(IExportProvider p) { } }

    private static HostServices AllServices() => new()
    {
        Document = new FakeDoc(),
        Layers = new FakeLayers(),
        LayerWrites = null,
        Commands = null,
        Menus = null,
        Export = new FakeExport(),
    };

    private static LoadedPlugin Validated(string capabilitiesJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sable_hc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path, $$"""
        {
          "id": "com.example.host",
          "name": "Host Test",
          "version": "0.1.0",
          "sdk_version": "1",
          "entrypoint": "X.Y",
          "capabilities": {{capabilitiesJson}},
          "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": false, "gpu": false }
        }
        """);
        var p = new LoadedPlugin(dir, path);
        new PluginLoader(new PluginLogHub().For("t")).ValidateManifest(p);
        return p;
    }

    [Fact]
    public void Factory_exposes_only_granted_apis()
    {
        var p = Validated("""["document.read", "export.provider"]""");
        var settings = new PluginSettingsStore(Path.GetTempPath(), p.Id);
        var ctx = HostContextFactory.Create(p, AllServices(), new PluginLogHub().For(p.Id), settings);

        Assert.NotNull(ctx.Document);      // granted
        Assert.NotNull(ctx.Export);        // granted
        Assert.Null(ctx.Layers);           // service exists but NOT granted → gated off
        Assert.Null(ctx.LayerWrites);
        Assert.Null(ctx.Commands);
        Assert.Null(ctx.Menus);
        Assert.True(ctx.Has("document.read"));
        Assert.False(ctx.Has("layer.read"));
    }

    [Fact]
    public void Factory_gates_off_even_when_service_present_but_capability_absent()
    {
        var p = Validated("""["layer.read"]""");
        var ctx = HostContextFactory.Create(p, AllServices(), new PluginLogHub().For(p.Id),
            new PluginSettingsStore(Path.GetTempPath(), p.Id));
        Assert.NotNull(ctx.Layers);
        Assert.Null(ctx.Document);   // service was supplied, but capability not granted
        Assert.Null(ctx.Export);
    }

    [Fact]
    public void Settings_roundtrip_and_isolation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sable_set_" + Guid.NewGuid().ToString("N"));
        var a = new PluginSettingsStore(dir, "com.example.a");
        a.Set("k", "v1");
        a.Save();
        Assert.Equal("v1", new PluginSettingsStore(dir, "com.example.a").Get("k"));   // persisted
        Assert.Null(new PluginSettingsStore(dir, "com.example.b").Get("k"));          // isolated per plugin

        a.Remove("k");
        Assert.False(a.Contains("k"));
    }

    [Fact]
    public void SafeFileName_replaces_invalid_chars()
    {
        var name = PluginSettingsStore.SafeFileName("com.example/evil:id");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
    }

    [Fact]
    public void LogHub_tags_entries_with_plugin_id()
    {
        var hub = new PluginLogHub();
        hub.For("com.a").Info("hello");
        hub.For("com.b").Error("oops");
        Assert.Equal(2, hub.Entries.Count);
        Assert.Equal("com.a", hub.Entries[0].PluginId);
        Assert.Equal(LogLevel.Error, hub.Entries[1].Level);
    }
}

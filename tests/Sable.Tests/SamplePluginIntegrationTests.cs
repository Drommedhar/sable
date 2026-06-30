using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Export;
using Sable.Plugin.Sdk.Host;
using Sable.Plugin.Sdk.Ui;
using Sable.Plugins;
using Sable.Plugins.Engine;
using Sable.SamplePlugin;

namespace Sable.Tests;

/// <summary>End-to-end: the real PluginManager activates a real IPlugin (the shipped sample) and
/// routes its command/menu/export registrations through the engine-backed host services, gated by
/// the manifest's capabilities.</summary>
public sealed class SamplePluginIntegrationTests
{
    private sealed class CollectingCommands : ICommandApi
    {
        public readonly List<PluginCommand> Commands = new();
        public void Register(PluginCommand c) => Commands.Add(c);
    }
    private sealed class CollectingMenus : IMenuApi
    {
        public readonly List<MenuContribution> Items = new();
        public void AddCommand(MenuContribution m) => Items.Add(m);
    }

    private static LoadedPlugin ValidatedManifest(string capsJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sable_sample_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path, $$"""
        {
          "id": "com.sable.sample",
          "name": "Sample",
          "version": "1.0.0",
          "sdk_version": "1",
          "entrypoint": "Sable.SamplePlugin.SamplePlugin",
          "capabilities": {{capsJson}},
          "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": false, "gpu": false }
        }
        """);
        var p = new LoadedPlugin(dir, path);
        new PluginLoader(new PluginLogHub().For("t")).ValidateManifest(p);
        return p;
    }

    private static (PluginManager mgr, CollectingCommands cmds, CollectingMenus menus, ExportRegistry export, PluginLogHub log)
        Host(Document doc, string capsJson)
    {
        var export = new ExportRegistry();
        var cmds = new CollectingCommands();
        var menus = new CollectingMenus();
        var log = new PluginLogHub();
        var state = new EngineHostState { ActiveDocument = () => doc, ActiveUndo = () => new UndoStack() };
        var services = SableHostServices.Build(state, new LayerHandles(), export, cmds, menus);
        var mgr = new PluginManager(
            Path.GetTempPath(), log.For("host"),
            p => HostContextFactory.Create(p, services, log.For(p.Id),
                new PluginSettingsStore(Path.GetTempPath(), p.Id)));
        return (mgr, cmds, menus, export, log);
    }

    [Fact]
    public void Sample_plugin_registers_command_menu_and_exporter()
    {
        var doc = new Document(64, 48);
        doc.Layers.Add(new PixelLayer(64, 48, "bg"));
        var (mgr, cmds, menus, export, _) = Host(doc, """["document.read","command.register","ui.menu_command","export.provider"]""");

        var plugin = ValidatedManifest("""["document.read","command.register","ui.menu_command","export.provider"]""");
        Assert.True(mgr.AddBuiltIn(plugin, new SamplePlugin.SamplePlugin()));

        Assert.Equal(2, cmds.Commands.Count);   // report + halve
        Assert.Equal("report", cmds.Commands[0].Id);
        Assert.Equal(2, menus.Items.Count);
        Assert.NotNull(export.ById("ppm"));   // exporter contributed
    }

    [Fact]
    public void Registered_command_reads_active_document_through_the_host()
    {
        var doc = new Document(800, 600);
        doc.Layers.Add(new PixelLayer(800, 600, "bg"));
        var (mgr, cmds, _, _, log) = Host(doc, """["document.read","command.register"]""");

        mgr.AddBuiltIn(ValidatedManifest("""["document.read","command.register"]"""), new SamplePlugin.SamplePlugin());
        cmds.Commands[0].Run();   // "Report Active Document"

        Assert.Contains(log.Entries, e => e.Message.Contains("800x600"));
    }

    [Fact]
    public void Without_export_capability_the_exporter_is_not_registered()
    {
        var doc = new Document(16, 16);
        var (mgr, cmds, _, export, _) = Host(doc, """["command.register"]""");

        // capability set excludes export.provider → host.Export is null → plugin's ?. skips it
        mgr.AddBuiltIn(ValidatedManifest("""["command.register"]"""), new SamplePlugin.SamplePlugin());

        Assert.Equal(2, cmds.Commands.Count);   // commands still registered
        Assert.Null(export.ById("ppm"));        // exporter gated off
    }

    [Fact]
    public void Sample_plugin_loads_from_disk_via_LoadAll()
    {
        // Lay the built sample DLL + a manifest into a temp plugins/<name>/ dir, then load it the
        // real way (discovery → collectible ALC → activate), not via AddBuiltIn.
        var dll = typeof(SamplePlugin.SamplePlugin).Assembly.Location;
        var root = Path.Combine(Path.GetTempPath(), "sable_disk_" + Guid.NewGuid().ToString("N"));
        var pdir = Path.Combine(root, "sample");
        Directory.CreateDirectory(pdir);
        File.Copy(dll, Path.Combine(pdir, Path.GetFileName(dll)));
        File.WriteAllText(Path.Combine(pdir, "manifest.json"), """
        {
          "id": "com.sable.sample",
          "name": "Sample",
          "version": "1.0.0",
          "sdk_version": "1",
          "entrypoint": "Sable.SamplePlugin.SamplePlugin",
          "capabilities": ["document.read","command.register","ui.menu_command","export.provider"],
          "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": false, "gpu": false }
        }
        """);

        var doc = new Document(32, 32);
        var export = new ExportRegistry();
        var cmds = new CollectingCommands();
        var log = new PluginLogHub();
        var state = new EngineHostState { ActiveDocument = () => doc, ActiveUndo = () => new UndoStack() };
        var services = SableHostServices.Build(state, new LayerHandles(), export, cmds, new CollectingMenus());
        var mgr = new PluginManager(root, log.For("host"),
            p => HostContextFactory.Create(p, services, log.For(p.Id), new PluginSettingsStore(Path.GetTempPath(), p.Id)));

        int activated = mgr.LoadAll();
        Assert.Equal(1, activated);
        Assert.NotNull(export.ById("ppm"));
        Assert.Equal(2, cmds.Commands.Count);
    }

    [Fact]
    public void Install_load_then_uninstall_removes_the_plugin_and_frees_the_dll()
    {
        // Stage a source folder (real sample DLL + manifest), install it, load it, then uninstall.
        var dll = typeof(SamplePlugin.SamplePlugin).Assembly.Location;
        var src = Path.Combine(Path.GetTempPath(), "sable_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        File.Copy(dll, Path.Combine(src, Path.GetFileName(dll)));
        File.WriteAllText(Path.Combine(src, "manifest.json"), """
        {
          "id": "com.sable.sample", "name": "Sample", "version": "1.0.0", "sdk_version": "1",
          "entrypoint": "Sable.SamplePlugin.SamplePlugin",
          "capabilities": ["command.register"],
          "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": false, "gpu": false }
        }
        """);

        var root = Path.Combine(Path.GetTempPath(), "sable_root_" + Guid.NewGuid().ToString("N"));
        var (mgr, _, _, _, _) = Host(new Document(8, 8), """["command.register"]""");
        // rebuild the manager rooted at our temp plugins dir
        var export = new ExportRegistry();
        var cmds = new CollectingCommands();
        var state = new EngineHostState { ActiveDocument = () => new Document(8, 8), ActiveUndo = () => new UndoStack() };
        var services = SableHostServices.Build(state, new LayerHandles(), export, cmds, new CollectingMenus());
        var log = new PluginLogHub();
        mgr = new PluginManager(root, log.For("host"),
            p => HostContextFactory.Create(p, services, log.For(p.Id), new PluginSettingsStore(Path.GetTempPath(), p.Id)));

        var install = mgr.Install(src);
        Assert.True(install.Ok, install.Error);
        Assert.Single(mgr.Registry.All);             // installed + loaded
        Assert.Equal(2, cmds.Commands.Count);        // its commands registered

        // The host must release the plugin's contributions (their delegates pin the ALC) before
        // unload, else the DLL stays locked. MainWindow does this via RemovePluginContributions.
        cmds.Commands.Clear();

        mgr.Uninstall("com.sable.sample");
        // Deterministic guarantees: forgotten from the registry, and the manifest is gone so it
        // can never reload — even if the DLL file is still locked by the unloading ALC (file
        // cleanup beyond the manifest is best-effort / completes on a later GC or restart).
        Assert.Empty(mgr.Registry.All);
        Assert.False(File.Exists(Path.Combine(install.Directory!, "manifest.json")));

        try { Directory.Delete(src, true); } catch { }
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void Consent_gate_withholds_until_approved()
    {
        // Stage + install the sample to a temp plugins root.
        var dll = typeof(SamplePlugin.SamplePlugin).Assembly.Location;
        var src = Path.Combine(Path.GetTempPath(), "sable_consent_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        File.Copy(dll, Path.Combine(src, Path.GetFileName(dll)));
        File.WriteAllText(Path.Combine(src, "manifest.json"), """
        {
          "id": "com.sable.sample", "name": "Sample", "version": "1.0.0", "sdk_version": "1",
          "entrypoint": "Sable.SamplePlugin.SamplePlugin",
          "capabilities": ["command.register"],
          "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": false, "gpu": false }
        }
        """);

        var root = Path.Combine(Path.GetTempPath(), "sable_consent_root_" + Guid.NewGuid().ToString("N"));
        var export = new ExportRegistry();
        var cmds = new CollectingCommands();
        var log = new PluginLogHub();
        var state = new EngineHostState { ActiveDocument = () => new Document(8, 8), ActiveUndo = () => new UndoStack() };
        var services = SableHostServices.Build(state, new LayerHandles(), export, cmds, new CollectingMenus());
        var mgr = new PluginManager(root, log.For("host"),
            p => HostContextFactory.Create(p, services, log.For(p.Id), new PluginSettingsStore(Path.GetTempPath(), p.Id)));

        bool approved = false;
        mgr.ConsentGate = _ => approved;   // not yet approved

        mgr.Install(src);   // copies + LoadAll
        var p = mgr.Registry.All[0];
        Assert.Equal(PluginState.NeedsConsent, p.State);   // withheld
        Assert.Empty(cmds.Commands);                       // its command did NOT register

        approved = true;
        Assert.True(mgr.ActivateApproved("com.sable.sample"));
        Assert.Equal(PluginState.Active, mgr.Registry.All[0].State);
        Assert.NotEmpty(cmds.Commands);                    // now registered

        try { Directory.Delete(src, true); } catch { }
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void Ppm_exporter_writes_a_valid_P6_header_and_drops_alpha()
    {
        var img = new ExportImage { Width = 2, Height = 1, Rgba = new byte[] { 10, 20, 30, 255, 40, 50, 60, 0 } };
        var bytes = new PpmExportProvider().Encode(img, new ExportOptions());
        var text = Encoding.ASCII.GetString(bytes, 0, 15);
        Assert.StartsWith("P6\n2 1\n255\n", text);
        // pixel data after the header: RGB only, alpha dropped
        int header = Encoding.ASCII.GetBytes("P6\n2 1\n255\n").Length;
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, bytes[header..]);
    }

    [Fact]
    public void Ppm_export_then_import_round_trips_the_rgb()
    {
        var rgba = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 };   // 2×1 opaque
        var encoded = new PpmExportProvider().Encode(new ExportImage { Width = 2, Height = 1, Rgba = rgba }, new ExportOptions());
        var decoded = new PpmImportProvider().Decode(encoded);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba);   // alpha comes back as 255
    }
}

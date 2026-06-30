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

        Assert.Single(cmds.Commands);
        Assert.Equal("report", cmds.Commands[0].Id);
        Assert.Single(menus.Items);
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

        Assert.Single(cmds.Commands);     // command still registered
        Assert.Null(export.ById("ppm"));  // exporter gated off
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
        Assert.Single(cmds.Commands);
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
}

using System;
using System.IO;
using System.IO.Compression;
using Sable.Plugins;

namespace Sable.Tests;

/// <summary>Plugin install from folder/.zip + manifest validation (PLUGIN_SDK_PLAN §24).</summary>
public sealed class PluginInstallerTests
{
    private const string ValidManifest = """
    {
      "id": "com.example.installme",
      "name": "Install Me",
      "version": "1.0.0",
      "sdk_version": "1",
      "entrypoint": "X.Y",
      "capabilities": ["command.register"],
      "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": false, "gpu": false }
    }
    """;

    private static string Temp() => Path.Combine(Path.GetTempPath(), "sable_inst_" + Guid.NewGuid().ToString("N"));

    private static string MakeSourceFolder()
    {
        var src = Temp();
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "manifest.json"), ValidManifest);
        File.WriteAllText(Path.Combine(src, "Plugin.dll"), "not a real dll");   // installer just copies files
        return src;
    }

    [Fact]
    public void Install_from_folder_copies_into_id_named_dir()
    {
        var dest = Temp();
        try
        {
            var r = PluginInstaller.Install(dest, MakeSourceFolder());
            Assert.True(r.Ok, r.Error);
            Assert.Equal("com.example.installme", Path.GetFileName(r.Directory));
            Assert.True(File.Exists(Path.Combine(r.Directory!, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(r.Directory!, "Plugin.dll")));
        }
        finally { try { Directory.Delete(dest, true); } catch { } }
    }

    [Fact]
    public void Install_from_zip_extracts()
    {
        var dest = Temp();
        var src = MakeSourceFolder();
        var zip = Temp() + ".zip";
        try
        {
            ZipFile.CreateFromDirectory(src, zip);
            var r = PluginInstaller.Install(dest, zip);
            Assert.True(r.Ok, r.Error);
            Assert.True(File.Exists(Path.Combine(r.Directory!, "manifest.json")));
        }
        finally { try { Directory.Delete(dest, true); } catch { } try { File.Delete(zip); } catch { } }
    }

    [Fact]
    public void Install_rejects_missing_manifest()
    {
        var dest = Temp();
        var src = Temp();
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "readme.txt"), "no manifest here");
        var r = PluginInstaller.Install(dest, src);
        Assert.False(r.Ok);
        Assert.Contains("manifest", r.Error);
    }

    [Fact]
    public void Install_rejects_invalid_manifest()
    {
        var dest = Temp();
        var src = Temp();
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "manifest.json"), """{ "name": "broken" }""");
        var r = PluginInstaller.Install(dest, src);
        Assert.False(r.Ok);
        Assert.Contains("invalid manifest", r.Error);
    }

    [Fact]
    public void Install_twice_reports_already_installed()
    {
        var dest = Temp();
        try
        {
            Assert.True(PluginInstaller.Install(dest, MakeSourceFolder()).Ok);
            var second = PluginInstaller.Install(dest, MakeSourceFolder());
            Assert.False(second.Ok);
            Assert.Contains("already installed", second.Error);
        }
        finally { try { Directory.Delete(dest, true); } catch { } }
    }
}

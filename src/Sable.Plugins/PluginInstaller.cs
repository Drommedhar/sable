using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Sable.Plugin.Sdk.Manifest;

namespace Sable.Plugins;

/// <summary>
/// Installs a plugin into the plugins root from a **folder** or a **.zip** (PLUGIN_SDK_PLAN §24).
/// The source must contain a `manifest.json` (at its root or one level down); the manifest is
/// validated and the install folder is named by the plugin's id, so a malformed plugin is rejected
/// up front and ids stay unique on disk. Pure filesystem — the host calls
/// <see cref="PluginManager.Install"/> which reloads afterwards.
/// </summary>
public static class PluginInstaller
{
    public readonly record struct InstallResult(bool Ok, string? Directory, string? Error);

    public static InstallResult Install(string pluginsDir, string source)
    {
        try
        {
            Directory.CreateDirectory(pluginsDir);
            if (Directory.Exists(source)) return FromManifestDir(pluginsDir, FindManifestDir(source), "the folder");
            if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return FromZip(pluginsDir, source);
            return new(false, null, "source must be a plugin folder or a .zip file");
        }
        catch (Exception ex) { return new(false, null, ex.Message); }
    }

    private static InstallResult FromZip(string pluginsDir, string zip)
    {
        var temp = Path.Combine(Path.GetTempPath(), "sable_pluginzip_" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zip, temp);
            return FromManifestDir(pluginsDir, FindManifestDir(temp), "the .zip");
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    private static InstallResult FromManifestDir(string pluginsDir, string? manifestDir, string what)
    {
        if (manifestDir is null) return new(false, null, $"no manifest.json found in {what}");

        var parsed = ManifestParser.Parse(File.ReadAllText(Path.Combine(manifestDir, "manifest.json")));
        if (!parsed.Ok) return new(false, null, "invalid manifest: " + string.Join("; ", parsed.Errors));

        var dest = Path.Combine(pluginsDir, SafeName(parsed.Manifest!.Id));
        if (Directory.Exists(dest))
            return new(false, dest, $"a plugin '{parsed.Manifest.Id}' is already installed — uninstall it first");

        CopyDir(manifestDir, dest);
        return new(true, dest, null);
    }

    /// <summary>The directory containing manifest.json: the given root, else a single sub-folder.</summary>
    private static string? FindManifestDir(string root)
    {
        if (File.Exists(Path.Combine(root, "manifest.json"))) return root;
        foreach (var sub in Directory.GetDirectories(root))
            if (File.Exists(Path.Combine(sub, "manifest.json"))) return sub;
        return null;
    }

    private static string SafeName(string id)
    {
        var chars = id.ToCharArray();
        foreach (var bad in Path.GetInvalidFileNameChars())
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] == bad) chars[i] = '_';
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "plugin" : s;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}

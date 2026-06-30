using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Manifest;

namespace Sable.Plugins;

/// <summary>
/// A plugin tracked by the host: its directory, manifest (once valid), instance (once loaded),
/// state, errors, and crash count. Created by <see cref="PluginDiscovery"/>, advanced by
/// <see cref="PluginLoader"/>, owned by <see cref="PluginRegistry"/>.
/// </summary>
public sealed class LoadedPlugin
{
    public LoadedPlugin(string directory, string manifestPath)
    {
        Directory = directory;
        ManifestPath = manifestPath;
    }

    public string Directory { get; }
    public string ManifestPath { get; }

    public PluginManifest? Manifest { get; internal set; }
    public IPlugin? Instance { get; internal set; }

    /// <summary>The collectible load context this plugin's assembly was loaded into (null for
    /// built-in/in-proc plugins). Kept so the host can unload it on uninstall, freeing the DLL
    /// file so it can be deleted.</summary>
    internal System.Runtime.Loader.AssemblyLoadContext? LoadContext { get; set; }

    public PluginState State { get; internal set; } = PluginState.Discovered;

    private readonly List<string> _errors = new();
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Times this plugin threw inside a host-guarded call this session.</summary>
    public int CrashCount { get; internal set; }

    /// <summary>Best id we have: manifest id once parsed, else the directory name.</summary>
    public string Id => Manifest?.Id ?? System.IO.Path.GetFileName(Directory.TrimEnd('/', '\\'));

    internal void AddError(string error) => _errors.Add(error);
    internal void AddErrors(IEnumerable<string> errors) => _errors.AddRange(errors);
    internal void ClearErrors() => _errors.Clear();
}

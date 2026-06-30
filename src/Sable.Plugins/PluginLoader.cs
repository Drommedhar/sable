using System.Reflection;
using System.Runtime.Loader;
using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Host;
using Sable.Plugin.Sdk.Manifest;

namespace Sable.Plugins;

/// <summary>
/// Validates a discovered plugin's manifest, loads its assembly, constructs the entrypoint, and
/// initializes it under the exception boundary. Each step advances <see cref="LoadedPlugin.State"/>
/// and records errors rather than throwing, so one bad plugin never aborts loading the rest
/// (PLUGIN_SDK_PLAN.md §24/§28). The host owns the IHostContext passed to <see cref="Activate"/>.
/// </summary>
public sealed class PluginLoader
{
    private readonly IPluginLogger _logger;

    public PluginLoader(IPluginLogger logger) => _logger = logger;

    /// <summary>
    /// Parse + validate <paramref name="plugin"/>'s manifest. On success sets
    /// <see cref="LoadedPlugin.Manifest"/>; on failure marks it <see cref="PluginState.Failed"/>.
    /// </summary>
    public bool ValidateManifest(LoadedPlugin plugin)
    {
        plugin.ClearErrors();
        string json;
        try
        {
            json = File.ReadAllText(plugin.ManifestPath);
        }
        catch (Exception ex)
        {
            return Fail(plugin, $"cannot read manifest: {ex.Message}");
        }

        var result = ManifestParser.Parse(json);
        if (!result.Ok)
        {
            plugin.AddErrors(result.Errors);
            plugin.State = PluginState.Failed;
            _logger.Warn($"plugin '{plugin.Id}' manifest invalid: {string.Join("; ", result.Errors)}");
            return false;
        }

        plugin.Manifest = result.Manifest;
        plugin.State = PluginState.Discovered;
        return true;
    }

    /// <summary>
    /// Load the plugin assembly and construct the entrypoint named by the manifest. Requires a
    /// valid <see cref="LoadedPlugin.Manifest"/>. Scans *.dll in the plugin directory for a type
    /// whose full name equals the manifest entrypoint and implements <see cref="IPlugin"/>.
    /// On success sets <see cref="LoadedPlugin.Instance"/> and state <see cref="PluginState.Loaded"/>.
    /// </summary>
    public bool Load(LoadedPlugin plugin)
    {
        var manifest = plugin.Manifest;
        if (manifest is null)
            return Fail(plugin, "cannot load before a valid manifest");

        var dlls = Directory.Exists(plugin.Directory)
            ? Directory.GetFiles(plugin.Directory, "*.dll", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        if (dlls.Length == 0)
            return Fail(plugin, "no assembly (*.dll) found in plugin directory");

        var alc = new AssemblyLoadContext($"sable-plugin:{manifest.Id}", isCollectible: true);
        Type? entry = null;
        foreach (var dll in dlls)
        {
            Assembly asm;
            try
            {
                asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
            }
            catch (Exception ex)
            {
                plugin.AddError($"failed to load {Path.GetFileName(dll)}: {ex.Message}");
                continue;
            }

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }

            foreach (var t in types)
            {
                if (t is null || t.FullName != manifest.Entrypoint) continue;
                if (!typeof(IPlugin).IsAssignableFrom(t))
                {
                    plugin.AddError($"entrypoint '{manifest.Entrypoint}' does not implement IPlugin");
                    continue;
                }
                entry = t;
                break;
            }
            if (entry is not null) break;
        }

        if (entry is null)
        {
            if (plugin.Errors.Count == 0)
                plugin.AddError($"entrypoint type '{manifest.Entrypoint}' not found in any assembly");
            plugin.State = PluginState.Failed;
            return false;
        }

        try
        {
            var instance = (IPlugin)Activator.CreateInstance(entry)!;
            plugin.LoadContext = alc;   // keep the ALC so we can unload it on uninstall
            return AttachInstance(plugin, instance);
        }
        catch (Exception ex)
        {
            return Fail(plugin, $"failed to construct entrypoint: {ex.Message}");
        }
    }

    /// <summary>Deactivate (if active) and unload the plugin's collectible load context, so its DLL
    /// file is no longer locked. The unload completes asynchronously after the GC reclaims it; the
    /// caller should GC + retry before deleting the file. Leaves the plugin <see cref="PluginState.Discovered"/>.</summary>
    public void Unload(LoadedPlugin plugin)
    {
        if (plugin.State == PluginState.Active) Deactivate(plugin);
        plugin.Instance = null;
        try { plugin.LoadContext?.Unload(); } catch { /* best-effort */ }
        plugin.LoadContext = null;
        plugin.State = PluginState.Discovered;
    }

    /// <summary>
    /// Attach an already-constructed instance (built-in/in-proc plugins, or tests) instead of
    /// loading from disk. Sets state <see cref="PluginState.Loaded"/>.
    /// </summary>
    public bool AttachInstance(LoadedPlugin plugin, IPlugin instance)
    {
        if (plugin.Manifest is null)
            return Fail(plugin, "cannot attach instance before a valid manifest");
        plugin.Instance = instance;
        plugin.State = PluginState.Loaded;
        return true;
    }

    /// <summary>
    /// Initialize the loaded plugin with <paramref name="host"/> under the exception boundary.
    /// No-ops (returns false) unless state is <see cref="PluginState.Loaded"/>. On success →
    /// <see cref="PluginState.Active"/>; a throw is caught (may quarantine) and leaves it not-Active.
    /// </summary>
    public bool Activate(LoadedPlugin plugin, IHostContext host)
    {
        if (plugin.State != PluginState.Loaded || plugin.Instance is null)
            return false;

        var ok = PluginGuard.Run(plugin, _logger, "Initialize", () => plugin.Instance!.Initialize(host));
        if (ok && plugin.State == PluginState.Loaded)
            plugin.State = PluginState.Active;
        return ok;
    }

    /// <summary>Shut down an active plugin under the boundary. Leaves it <see cref="PluginState.Loaded"/>.</summary>
    public void Deactivate(LoadedPlugin plugin)
    {
        if (plugin.Instance is null || plugin.State != PluginState.Active) return;
        PluginGuard.Run(plugin, _logger, "Shutdown", () => plugin.Instance!.Shutdown());
        if (plugin.State == PluginState.Active)
            plugin.State = PluginState.Loaded;
    }

    private bool Fail(LoadedPlugin plugin, string error)
    {
        plugin.AddError(error);
        plugin.State = PluginState.Failed;
        _logger.Warn($"plugin '{plugin.Id}' failed: {error}");
        return false;
    }
}

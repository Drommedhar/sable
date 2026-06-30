using Sable.Plugin.Sdk.Host;

namespace Sable.Plugins;

/// <summary>
/// Orchestrates the plugin platform end to end: discover → validate → load → activate, plus
/// enable/disable and safe-mode (PLUGIN_SDK_PLAN.md §24). The app owns one of these. The host
/// supplies a per-plugin <see cref="IHostContext"/> via <paramref name="contextFactory"/> — that
/// is the engine-backed adapter wired in a later pass; this class never touches the engine.
///
/// Safe mode (<see cref="SafeMode"/>) discovers + validates but does NOT load assemblies or
/// activate, so a broken plugin set can't block startup.
/// </summary>
public sealed class PluginManager
{
    private readonly string _rootDir;
    private readonly IPluginLogger _logger;
    private readonly PluginLoader _loader;
    private readonly Func<LoadedPlugin, IHostContext> _contextFactory;

    public PluginManager(string rootDir, IPluginLogger logger, Func<LoadedPlugin, IHostContext> contextFactory)
    {
        _rootDir = rootDir;
        _logger = logger;
        _contextFactory = contextFactory;
        _loader = new PluginLoader(logger);
    }

    public PluginRegistry Registry { get; } = new();

    /// <summary>When true, plugins are discovered + validated but never loaded/activated.</summary>
    public bool SafeMode { get; set; }

    /// <summary>
    /// Run the full pipeline over the plugins root. Idempotent-ish: only adds plugins not already
    /// in the registry. Returns the number activated.
    /// </summary>
    public int LoadAll()
    {
        int activated = 0;
        foreach (var candidate in PluginDiscovery.Discover(_rootDir))
        {
            if (!_loader.ValidateManifest(candidate))
            {
                AddSafe(candidate);
                continue;
            }
            if (Registry.Contains(candidate.Id))
            {
                _logger.Warn($"duplicate plugin id '{candidate.Id}' at {candidate.Directory} — skipped");
                continue;
            }
            Registry.Add(candidate);

            if (SafeMode) continue;
            if (!_loader.Load(candidate)) continue;
            if (_loader.Activate(candidate, _contextFactory(candidate)))
                activated++;
        }
        return activated;
    }

    /// <summary>Activate a built-in/in-proc plugin not loaded from disk (manifest already set).</summary>
    public bool AddBuiltIn(LoadedPlugin plugin, Sable.Plugin.Sdk.IPlugin instance)
    {
        Registry.Add(plugin);
        if (SafeMode || !_loader.AttachInstance(plugin, instance)) return false;
        return _loader.Activate(plugin, _contextFactory(plugin));
    }

    public void Disable(string id)
    {
        var p = Registry.Get(id);
        if (p is { State: PluginState.Active }) _loader.Deactivate(p);
        Registry.Disable(id);
    }

    public bool Enable(string id)
    {
        var p = Registry.Get(id);
        if (p is null) return false;
        Registry.Enable(id);
        if (SafeMode) return false;
        if (p.Instance is null && p.Manifest is not null && !_loader.Load(p)) return false;
        return _loader.Activate(p, _contextFactory(p));
    }

    /// <summary>Install a plugin from a folder or .zip into the plugins root, then load it.
    /// Returns the install result (folder + any error). On success the plugin is loaded + activated.</summary>
    public PluginInstaller.InstallResult Install(string source)
    {
        var result = PluginInstaller.Install(_rootDir, source);
        if (result.Ok) LoadAll();   // pick up the newly installed folder
        return result;
    }

    /// <summary>Uninstall a plugin: deactivate + unload its load context, forget it, and delete its
    /// folder from disk. The manifest is deleted first so it can't reload even if the DLL file is
    /// still locked (collectible-ALC unload finalises asynchronously). Returns true if the folder
    /// was fully removed; false means a restart is needed to clear the leftover files.</summary>
    public bool Uninstall(string id)
    {
        var p = Registry.Get(id);
        if (p is null) return false;
        var dir = p.Directory;

        _loader.Unload(p);
        Registry.Remove(id);

        try { System.IO.File.Delete(System.IO.Path.Combine(dir, "manifest.json")); } catch { }

        for (int attempt = 0; attempt < 3 && System.IO.Directory.Exists(dir); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { System.IO.Directory.Delete(dir, recursive: true); }
            catch { /* DLL still locked by the unloading ALC — retry */ }
        }
        return !System.IO.Directory.Exists(dir);
    }

    public void ShutdownAll()
    {
        foreach (var p in Registry.All)
            if (p.State == PluginState.Active)
                _loader.Deactivate(p);
    }

    private void AddSafe(LoadedPlugin plugin)
    {
        // Failed-manifest plugins keep their (directory-name) id; avoid a duplicate-id throw.
        if (!Registry.Contains(plugin.Id)) Registry.Add(plugin);
    }
}

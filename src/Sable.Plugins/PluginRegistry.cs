namespace Sable.Plugins;

/// <summary>
/// In-memory set of all plugins known this session, keyed by id, plus enable/disable/quarantine
/// state transitions. Owned by <see cref="PluginManager"/>. Pure state — no IO, no loading.
/// </summary>
public sealed class PluginRegistry
{
    /// <summary>Crash count at which a plugin is auto-quarantined (PLUGIN_SDK_PLAN.md §12.2).</summary>
    public const int CrashThreshold = 3;

    private readonly Dictionary<string, LoadedPlugin> _byId = new(StringComparer.Ordinal);
    private readonly List<LoadedPlugin> _order = new();

    public IReadOnlyList<LoadedPlugin> All => _order;

    public LoadedPlugin? Get(string id) => _byId.TryGetValue(id, out var p) ? p : null;

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>Add a plugin. Throws on duplicate id (manifest ids must be unique).</summary>
    public void Add(LoadedPlugin plugin)
    {
        var id = plugin.Id;
        if (_byId.ContainsKey(id))
            throw new InvalidOperationException($"duplicate plugin id: {id}");
        _byId[id] = plugin;
        _order.Add(plugin);
    }

    /// <summary>User-disable a plugin (persists across the toggle; does not unload errors).</summary>
    public void Disable(string id)
    {
        if (_byId.TryGetValue(id, out var p))
            p.State = PluginState.Disabled;
    }

    /// <summary>Re-enable a disabled/quarantined plugin, resetting its crash count.</summary>
    public void Enable(string id)
    {
        if (!_byId.TryGetValue(id, out var p)) return;
        if (p.State is PluginState.Disabled or PluginState.Quarantined)
        {
            p.CrashCount = 0;
            p.State = p.Instance is not null ? PluginState.Loaded : PluginState.Discovered;
        }
    }

    public bool IsRunnable(LoadedPlugin p)
        => p.State is PluginState.Discovered or PluginState.Loaded or PluginState.Active;
}

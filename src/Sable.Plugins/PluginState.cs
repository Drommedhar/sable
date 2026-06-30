namespace Sable.Plugins;

/// <summary>Lifecycle state of a discovered plugin (PLUGIN_SDK_PLAN.md §24/§28).</summary>
public enum PluginState
{
    /// <summary>Manifest found but not yet validated/loaded.</summary>
    Discovered,
    /// <summary>Manifest invalid, assembly missing, or entrypoint not found. See <c>LoadedPlugin.Errors</c>.</summary>
    Failed,
    /// <summary>Manifest valid + assembly loaded + instance constructed, not yet initialized.</summary>
    Loaded,
    /// <summary>Initialized and running.</summary>
    Active,
    /// <summary>User-disabled. Stays installed; not initialized.</summary>
    Disabled,
    /// <summary>Auto-disabled after repeated crashes (PLUGIN_SDK_PLAN.md §12.2).</summary>
    Quarantined,
}

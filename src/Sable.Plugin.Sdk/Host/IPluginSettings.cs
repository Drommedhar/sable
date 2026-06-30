namespace Sable.Plugin.Sdk.Host;

/// <summary>
/// Per-plugin key/value settings. The host persists these in a namespace private to the
/// plugin (PLUGIN_SDK_PLAN.md §12 / boundary map §2.6) so plugins can't read each other's
/// or the host's settings. Values are strings; encode structured data as JSON yourself.
/// </summary>
public interface IPluginSettings
{
    string? Get(string key);
    void Set(string key, string? value);
    bool Contains(string key);
    void Remove(string key);

    /// <summary>Flush to disk. The host may also persist on shutdown.</summary>
    void Save();
}

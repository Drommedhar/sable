using Sable.Plugin.Sdk.Host;

namespace Sable.Plugins;

/// <summary>
/// Per-plugin exception boundary (PLUGIN_SDK_PLAN.md §12.2). Wraps every call into plugin code
/// so a throwing plugin cannot crash the host: the error is logged, the crash counted, and the
/// plugin quarantined once <see cref="PluginRegistry.CrashThreshold"/> is hit. Returns whether
/// the call completed without throwing.
/// </summary>
public static class PluginGuard
{
    /// <summary>Run a plugin action under the boundary. Returns true on success.</summary>
    public static bool Run(LoadedPlugin plugin, IPluginLogger logger, string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            plugin.CrashCount++;
            plugin.AddError($"{what}: {ex.GetType().Name}: {ex.Message}");
            logger.Error($"plugin '{plugin.Id}' threw during {what}", ex);
            if (plugin.CrashCount >= PluginRegistry.CrashThreshold)
            {
                plugin.State = PluginState.Quarantined;
                logger.Warn($"plugin '{plugin.Id}' quarantined after {plugin.CrashCount} crashes");
            }
            return false;
        }
    }
}

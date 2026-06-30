using Sable.Plugin.Sdk.Host;

namespace Sable.Plugin.Sdk;

/// <summary>
/// Entry point every plugin implements. The host instantiates the type named by the
/// manifest <c>entrypoint</c> (parameterless ctor) and calls <see cref="Initialize"/> once
/// after capabilities are granted, then <see cref="Shutdown"/> on unload/disable.
///
/// Contract:
/// - <see cref="Initialize"/> runs on the host UI thread. Register commands / menu items /
///   export providers here via the APIs on <paramref name="host"/>. Do NOT block.
/// - Heavy or long work belongs in the registered command/batch handlers, not here.
/// - Throwing from either method is caught by the host's per-plugin boundary; repeated
///   crashes quarantine the plugin (PLUGIN_SDK_PLAN.md §12.2).
/// </summary>
public interface IPlugin
{
    void Initialize(IHostContext host);

    void Shutdown();
}

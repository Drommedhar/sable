using System;
using System.Collections.Generic;
using Sable.Plugin.Sdk.Automation;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Document;
using Sable.Plugin.Sdk.Export;
using Sable.Plugin.Sdk.Import;
using Sable.Plugin.Sdk.Ui;
using Sable.Plugins;

namespace Sable.App;

/// <summary>UI-side <see cref="ICommandApi"/>: forwards a plugin's command registration to the host
/// (MainWindow buckets it per plugin for the palette + clean uninstall).</summary>
internal sealed class AppCommandApi : ICommandApi
{
    private readonly Action<PluginCommand> _add;
    public AppCommandApi(Action<PluginCommand> add) => _add = add;
    public void Register(PluginCommand command) => _add(command);
}

/// <summary>UI-side <see cref="IMenuApi"/>: forwards a plugin's menu contribution to the host
/// (MainWindow adds it under the Plugins menu, tracked per plugin).</summary>
internal sealed class AppMenuApi : IMenuApi
{
    private readonly Action<MenuContribution> _add;
    public AppMenuApi(Action<MenuContribution> add) => _add = add;
    public void AddCommand(MenuContribution item) => _add(item);
}

/// <summary>UI-side <see cref="IExportApi"/>: registers the provider with the shared
/// <see cref="ExportRegistry"/> and records its id against the plugin so uninstall can unregister
/// it (releasing the ALC-rooted delegate/type so the assembly can unload).</summary>
internal sealed class AppExportApi : IExportApi
{
    private readonly ExportRegistry _registry;
    private readonly Action<string> _recordId;
    public AppExportApi(ExportRegistry registry, Action<string> recordId)
    {
        _registry = registry;
        _recordId = recordId;
    }
    public void Register(IExportProvider provider)
    {
        _registry.Register(provider);
        _recordId(provider.Id);
    }
}

/// <summary>UI-side <see cref="IImportApi"/>: registers the provider with the shared
/// <see cref="ImportRegistry"/> and records its id against the plugin (for clean uninstall).</summary>
internal sealed class AppImportApi : IImportApi
{
    private readonly ImportRegistry _registry;
    private readonly Action<string> _recordId;
    public AppImportApi(ImportRegistry registry, Action<string> recordId)
    {
        _registry = registry;
        _recordId = recordId;
    }
    public void Register(IImportProvider provider)
    {
        _registry.Register(provider);
        _recordId(provider.Id);
    }
}

/// <summary>UI-side <see cref="IBatchRegistry"/>: forwards a plugin's batch-operation registration to
/// the host (MainWindow buckets it per plugin for the Batch UI + clean uninstall).</summary>
internal sealed class AppBatchApi : IBatchRegistry
{
    private readonly Action<BatchOperation> _add;
    public AppBatchApi(Action<BatchOperation> add) => _add = add;
    public void Register(BatchOperation operation) => _add(operation);
}

/// <summary>UI-side <see cref="IDocumentEvents"/>: a plugin's change handlers are registered with the
/// shared <see cref="DocumentEventHub"/> under the plugin id (so uninstall drops them), each wrapped
/// in the host's per-plugin crash guard.</summary>
internal sealed class AppDocumentEvents : IDocumentEvents
{
    private readonly string _id;
    private readonly DocumentEventHub _hub;
    private readonly Action<string, Action> _guard;   // (pluginId, handler) → run guarded
    public AppDocumentEvents(string id, DocumentEventHub hub, Action<string, Action> guard)
    {
        _id = id;
        _hub = hub;
        _guard = guard;
    }
    public void OnDocumentChanged(Action h) => _hub.OnDocumentChanged(_id, () => _guard(_id, h));
    public void OnSelectionChanged(Action h) => _hub.OnSelectionChanged(_id, () => _guard(_id, h));
    public void OnActiveDocumentChanged(Action h) => _hub.OnActiveDocumentChanged(_id, () => _guard(_id, h));
}

/// <summary>The plugin-management surface the Settings ▸ Plugins page drives. Implemented by
/// MainWindow (it owns the live <see cref="PluginManager"/> + host services + UI contributions).</summary>
internal interface IPluginAdmin
{
    /// <summary>Global on/off. Setting it builds/tears down the plugin host live.</summary>
    bool Enabled { get; set; }

    string PluginsDir { get; }

    /// <summary>Installed plugins (empty when disabled / none loaded).</summary>
    IReadOnlyList<LoadedPlugin> List();

    /// <summary>The most recent log entries this plugin emitted (newest last), capped to
    /// <paramref name="max"/>. Empty when the host isn't running / no logs yet.</summary>
    IReadOnlyList<PluginLogEntry> Logs(string id, int max);

    void Enable(string id);
    void Disable(string id);

    /// <summary>Record the user's approval of a plugin's requested capabilities/permissions and
    /// activate it (was <see cref="PluginState.NeedsConsent"/>).</summary>
    void Approve(string id);

    /// <summary>Remove a plugin's contributions + unload + delete its folder. True = fully removed.</summary>
    bool Uninstall(string id);

    /// <summary>Install from a folder or .zip; loads it if the host is enabled.</summary>
    PluginInstaller.InstallResult Install(string source);

    /// <summary>Re-scan the plugins folder for newly added plugins.</summary>
    void Reload();
}

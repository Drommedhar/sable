using System;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Ui;

namespace Sable.App;

/// <summary>UI-side <see cref="ICommandApi"/>: forwards a plugin's command registration to the host
/// (MainWindow adds it to the command palette).</summary>
internal sealed class AppCommandApi : ICommandApi
{
    private readonly Action<PluginCommand> _add;
    public AppCommandApi(Action<PluginCommand> add) => _add = add;
    public void Register(PluginCommand command) => _add(command);
}

/// <summary>UI-side <see cref="IMenuApi"/>: forwards a plugin's menu contribution to the host
/// (MainWindow adds it under the Plugins menu).</summary>
internal sealed class AppMenuApi : IMenuApi
{
    private readonly Action<MenuContribution> _add;
    public AppMenuApi(Action<MenuContribution> add) => _add = add;
    public void AddCommand(MenuContribution item) => _add(item);
}

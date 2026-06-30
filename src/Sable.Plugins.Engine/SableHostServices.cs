using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Ui;
using Sable.Plugins;

namespace Sable.Plugins.Engine;

/// <summary>
/// Assembles a <see cref="HostServices"/> bundle from the engine-backed adapters. The app calls
/// <see cref="Build"/> once (pointing <see cref="EngineHostState"/> at its active tab) and hands
/// the result to <see cref="HostContextFactory"/> via <c>PluginManager</c>'s context factory.
/// Command/menu APIs are UI-side (Avalonia) so the app passes them in; the rest are built here.
/// </summary>
public static class SableHostServices
{
    public static HostServices Build(
        EngineHostState state, LayerHandles handles, ExportRegistry export,
        ICommandApi? commands = null, IMenuApi? menus = null,
        Sable.Plugin.Sdk.Import.IImportApi? import = null)
    {
        var txn = new PluginTransaction();
        return new HostServices
        {
            Document = new EngineDocumentApi(state),
            Layers = new EngineLayerApi(state, handles),
            LayerWrites = new EngineLayerWriteApi(state, handles, txn),
            Export = export,
            Import = import,
            Commands = commands,
            Menus = menus,
            Selection = new EngineSelectionApi(state),
            Pixels = new EnginePixelApi(state),
            Transactions = new EngineTransactionApi(state, txn),
        };
    }
}

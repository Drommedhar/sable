using Sable.Plugin.Sdk.Capabilities;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Document;
using Sable.Plugin.Sdk.Export;
using Sable.Plugin.Sdk.Host;
using Sable.Plugin.Sdk.Import;
using Sable.Plugin.Sdk.Layers;
using Sable.Plugin.Sdk.Manifest;
using Sable.Plugin.Sdk.Pixels;
using Sable.Plugin.Sdk.Selection;
using Sable.Plugin.Sdk.Ui;

namespace Sable.Plugins;

/// <summary>
/// The concrete <see cref="IHostContext"/> handed to a plugin in <c>Initialize</c>. Pure plumbing:
/// it just exposes the manifest, logger, settings and the capability-gated API objects it was
/// constructed with. The engine-backed API implementations come from the app (via
/// <see cref="HostServices"/>); this class stays Avalonia/engine-free so the gating is testable.
/// </summary>
public sealed class HostContext : IHostContext
{
    public HostContext(
        PluginManifest manifest, IPluginLogger logger, IPluginSettings settings,
        IDocumentApi? document, ILayerApi? layers, ILayerWriteApi? layerWrites,
        ICommandApi? commands, IMenuApi? menus, IExportApi? export, IImportApi? import,
        ISelectionApi? selection, IPixelApi? pixels, ITransactionApi? transactions)
    {
        Manifest = manifest;
        Logger = logger;
        Settings = settings;
        Document = document;
        Layers = layers;
        LayerWrites = layerWrites;
        Commands = commands;
        Menus = menus;
        Export = export;
        Import = import;
        Selection = selection;
        Pixels = pixels;
        Transactions = transactions;
    }

    public PluginManifest Manifest { get; }
    public IPluginLogger Logger { get; }
    public IPluginSettings Settings { get; }

    public bool Has(string capability) => Manifest.HasCapability(capability);

    public IDocumentApi? Document { get; }
    public ILayerApi? Layers { get; }
    public ILayerWriteApi? LayerWrites { get; }
    public ICommandApi? Commands { get; }
    public IMenuApi? Menus { get; }
    public IExportApi? Export { get; }
    public IImportApi? Import { get; }
    public ISelectionApi? Selection { get; }
    public IPixelApi? Pixels { get; }
    public ITransactionApi? Transactions { get; }
}

/// <summary>
/// The host's full set of engine-backed API implementations. The app builds one of these (its
/// adapters over Document / UndoStack / the command palette / <see cref="ExportRegistry"/>) and
/// hands it to <see cref="HostContextFactory"/>, which exposes each API to a plugin ONLY if that
/// plugin was granted the matching capability.
/// </summary>
public sealed class HostServices
{
    public IDocumentApi? Document { get; init; }
    public ILayerApi? Layers { get; init; }
    public ILayerWriteApi? LayerWrites { get; init; }
    public ICommandApi? Commands { get; init; }
    public IMenuApi? Menus { get; init; }
    public IExportApi? Export { get; init; }
    public IImportApi? Import { get; init; }
    public ISelectionApi? Selection { get; init; }
    public IPixelApi? Pixels { get; init; }
    public ITransactionApi? Transactions { get; init; }
}

/// <summary>
/// Builds a per-plugin <see cref="HostContext"/> with capability gating: an API is passed through
/// only when the plugin declared the matching <see cref="Capability"/>. This is the function the
/// app gives <see cref="PluginManager"/> as its <c>contextFactory</c>.
/// </summary>
public static class HostContextFactory
{
    public static IHostContext Create(
        LoadedPlugin plugin, HostServices services,
        IPluginLogger logger, IPluginSettings settings)
    {
        var m = plugin.Manifest
            ?? throw new System.InvalidOperationException("cannot build a host context before the manifest is parsed");

        bool G(string cap) => m.HasCapability(cap);
        return new HostContext(m, logger, settings,
            G(Capability.DocumentRead) ? services.Document : null,
            G(Capability.LayerRead) ? services.Layers : null,
            G(Capability.LayerWriteBasic) ? services.LayerWrites : null,
            G(Capability.CommandRegister) ? services.Commands : null,
            G(Capability.UiMenuCommand) ? services.Menus : null,
            G(Capability.ExportProvider) ? services.Export : null,
            G(Capability.ImportProvider) ? services.Import : null,
            G(Capability.SelectionRead) ? services.Selection : null,
            G(Capability.PixelRead) ? services.Pixels : null,
            G(Capability.UndoTransaction) ? services.Transactions : null);
    }
}

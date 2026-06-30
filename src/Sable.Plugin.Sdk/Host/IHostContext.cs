using Sable.Plugin.Sdk.Automation;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Document;
using Sable.Plugin.Sdk.Export;
using Sable.Plugin.Sdk.Import;
using Sable.Plugin.Sdk.Layers;
using Sable.Plugin.Sdk.Manifest;
using Sable.Plugin.Sdk.Pixels;
using Sable.Plugin.Sdk.Selection;
using Sable.Plugin.Sdk.Ui;

namespace Sable.Plugin.Sdk.Host;

/// <summary>
/// The host surface a plugin receives in <see cref="IPlugin.Initialize"/>. Capability gating
/// happens HERE: an API property is non-null only when the plugin declared AND was granted the
/// matching capability (PLUGIN_SDK_PLAN.md §10/§14). Read <see cref="Manifest"/>'s capabilities
/// or call <see cref="Has"/> before assuming an API is present; touching a null one is a plugin
/// bug. <see cref="Logger"/> and <see cref="Settings"/> are always available.
///
/// Threading: all members are called on the host UI thread. To do background work, the plugin
/// spawns its own task and marshals results back via a host callback (the host serialises edits).
/// </summary>
public interface IHostContext
{
    PluginManifest Manifest { get; }

    IPluginLogger Logger { get; }
    IPluginSettings Settings { get; }

    /// <summary>True when the plugin holds the given <see cref="Capabilities.Capability"/> id.</summary>
    bool Has(string capability);

    // --- Capability-gated APIs (null when not granted) ---

    IDocumentApi? Document { get; }     // document.read
    ILayerApi? Layers { get; }          // layer.read
    ILayerWriteApi? LayerWrites { get; }// layer.write.basic
    ICommandApi? Commands { get; }      // command.register
    IMenuApi? Menus { get; }            // ui.menu_command
    IExportApi? Export { get; }         // export.provider
    IImportApi? Import { get; }         // import.provider
    ISelectionApi? Selection { get; }   // selection.read
    IPixelApi? Pixels { get; }          // pixel.read
    IPixelWriteApi? PixelWrites { get; }// pixel.write.layer_output
    ITransactionApi? Transactions { get; } // undo.transaction
    IBatchRegistry? Automation { get; } // automation.batch
}

namespace Sable.Plugin.Sdk.Capabilities;

/// <summary>
/// Capability identifiers (PLUGIN_SDK_PLAN.md §14). A plugin requests a subset in its
/// manifest; the host grants only requested + user-approved capabilities and exposes the
/// matching API surface (a null API on <see cref="Host.IHostContext"/> = not granted).
///
/// Identifiers are stable strings — never renumber, only add. Unknown ids in a manifest
/// are a load error so typos and future-version capabilities fail loudly on an older host.
/// </summary>
public static class Capability
{
    // P0 — first SDK version (must ship).
    public const string DocumentRead = "document.read";
    public const string LayerRead = "layer.read";
    public const string LayerWriteBasic = "layer.write.basic";
    public const string CommandRegister = "command.register";
    public const string AutomationBatch = "automation.batch";
    public const string ExportProvider = "export.provider";
    public const string ImportProvider = "import.provider";
    public const string UiMenuCommand = "ui.menu_command";

    // P1 — workflow tooling (declared now so manifests validate; APIs land later).
    public const string SelectionRead = "selection.read";
    public const string PixelRead = "pixel.read";
    public const string PixelWriteLayerOutput = "pixel.write.layer_output";
    public const string UiPanel = "ui.panel";
    public const string UndoTransaction = "undo.transaction";
    public const string DocumentEvents = "document.events";

    // P2 — image-processing extensibility.
    public const string FilterNode = "filter.node";
    public const string GeneratorNode = "generator.node";
    public const string GpuCompute = "gpu.compute";
    public const string ExternalToolBridge = "external_tool.bridge";

    /// <summary>Every capability id the current SDK knows about.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentRead, LayerRead, LayerWriteBasic, CommandRegister, AutomationBatch,
        ExportProvider, ImportProvider, UiMenuCommand,
        SelectionRead, PixelRead, PixelWriteLayerOutput, UiPanel, UndoTransaction, DocumentEvents,
        FilterNode, GeneratorNode, GpuCompute, ExternalToolBridge,
    };

    /// <summary>Capabilities backed by a real host API surfaced on <see cref="Host.IHostContext"/>.</summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentRead, LayerRead, LayerWriteBasic, CommandRegister, AutomationBatch,
        ExportProvider, ImportProvider, UiMenuCommand,
        SelectionRead, PixelRead, PixelWriteLayerOutput, UndoTransaction, DocumentEvents,
    };

    public static bool IsKnown(string id) => Known.Contains(id);
}

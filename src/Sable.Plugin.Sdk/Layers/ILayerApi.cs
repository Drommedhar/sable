namespace Sable.Plugin.Sdk.Layers;

/// <summary>
/// Read access to the active document's layers (capability <c>layer.read</c>). Null on
/// <see cref="Host.IHostContext.Layers"/> when not granted.
/// </summary>
public interface ILayerApi
{
    /// <summary>All layers of the active document, flattened depth-first, bottom→top.</summary>
    IReadOnlyList<LayerInfo> All();

    /// <summary>The single selected layer, or null (none / multi-selection).</summary>
    LayerInfo? Selected();

    LayerInfo? ById(string id);
}

/// <summary>
/// Basic layer mutation (capability <c>layer.write.basic</c>). Null on
/// <see cref="Host.IHostContext.LayerWrites"/> when not granted. EVERY method routes through
/// the host's undo stack as one undoable step — plugins never bypass the editing model
/// (PLUGIN_SDK_PLAN.md §13). Ids come from <see cref="ILayerApi"/>; an unknown/stale id throws.
/// </summary>
public interface ILayerWriteApi
{
    void SetName(string id, string name);
    void SetOpacity(string id, float opacity);     // clamped 0..1 by host
    void SetFillOpacity(string id, float opacity); // clamped 0..1 by host
    void SetBlend(string id, SdkBlendMode mode);
    void SetVisible(string id, bool visible);

    /// <summary>Create an empty pixel layer. Returns the new layer's id.</summary>
    /// <param name="parentId">Group to insert into, or null for the document root.</param>
    /// <param name="index">Insertion index within the parent (clamped), or -1 for top.</param>
    string AddPixelLayer(string name, string? parentId = null, int index = -1);

    void Remove(string id);

    /// <summary>Reorder within the layer's current parent by <paramref name="delta"/> steps.</summary>
    void Move(string id, int delta);
}

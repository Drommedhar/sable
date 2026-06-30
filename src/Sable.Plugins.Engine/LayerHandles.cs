using System.Collections.Generic;
using Sable.Engine.Layers;

namespace Sable.Plugins.Engine;

/// <summary>
/// Assigns each <see cref="Layer"/> a stable opaque string id for the session and resolves ids
/// back to layers. Ids are what plugins see in <c>LayerInfo</c> and pass to the write API — the
/// engine's <see cref="Layer"/> objects never cross the SDK boundary. Keyed by reference identity,
/// so the same layer always maps to the same id; a removed layer's id simply stops resolving.
/// </summary>
public sealed class LayerHandles
{
    private readonly Dictionary<Layer, string> _toId = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Layer> _toLayer = new();
    private int _next = 1;

    public string IdFor(Layer layer)
    {
        if (_toId.TryGetValue(layer, out var id)) return id;
        id = "L" + _next++;
        _toId[layer] = id;
        _toLayer[id] = layer;
        return id;
    }

    /// <summary>Resolve an id to its layer, or null if unknown/stale.</summary>
    public Layer? Resolve(string id) => _toLayer.TryGetValue(id, out var l) ? l : null;
}

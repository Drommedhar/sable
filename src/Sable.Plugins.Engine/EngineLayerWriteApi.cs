using System;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Layers;

namespace Sable.Plugins.Engine;

/// <summary>Undoable set of a single layer property (capability <c>layer.write.basic</c>): captures
/// the old value, applies the new on Do and restores on Undo, flagging the layer dirty either way
/// so the compositor repaints.</summary>
internal sealed class LayerPropCommand<T> : IUndoableCommand
{
    private readonly Layer _layer;
    private readonly T _old, _new;
    private readonly Action<Layer, T> _set;

    public LayerPropCommand(Layer layer, string name, T oldValue, T newValue, Action<Layer, T> set)
    {
        _layer = layer; Name = name; _old = oldValue; _new = newValue; _set = set;
    }

    public string Name { get; }
    public void Do() { _set(_layer, _new); _layer.Dirty = true; }
    public void Undo() { _set(_layer, _old); _layer.Dirty = true; }
}

/// <summary>
/// Basic layer mutation for plugins (capability <c>layer.write.basic</c>). Every method routes
/// through the active document's <see cref="UndoStack"/> as ONE undoable step — plugins never
/// touch the layer tree directly (PLUGIN_SDK_PLAN.md §13). Ids come from <see cref="ILayerApi"/>;
/// an unknown/stale id throws.
/// </summary>
public sealed class EngineLayerWriteApi : ILayerWriteApi
{
    private readonly EngineHostState _state;
    private readonly LayerHandles _handles;
    private readonly PluginTransaction? _txn;

    public EngineLayerWriteApi(EngineHostState state, LayerHandles handles, PluginTransaction? txn = null)
    {
        _state = state;
        _handles = handles;
        _txn = txn;
    }

    /// <summary>Execute now, or buffer into the open transaction so the whole batch is one undo step.
    /// Buffered commands are applied (Do) + recorded together at commit by <see cref="EngineTransactionApi"/>.</summary>
    private void Submit(IUndoableCommand cmd)
    {
        if (_txn?.Pending is { } pending) pending.Add(cmd);
        else Active().undo.Execute(cmd);
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    private Layer Require(string id)
        => _handles.Resolve(id) ?? throw new ArgumentException($"unknown layer id: {id}", nameof(id));

    private (Document doc, UndoStack undo) Active()
    {
        var doc = _state.ActiveDocument() ?? throw new InvalidOperationException("no active document");
        var undo = _state.ActiveUndo() ?? throw new InvalidOperationException("no active undo stack");
        return (doc, undo);
    }

    private void Edit<T>(string id, string name, Func<Layer, T> get, Action<Layer, T> set, T value)
    {
        var l = Require(id);
        Submit(new LayerPropCommand<T>(l, name, get(l), value, set));
    }

    public void SetName(string id, string name)
        => Edit(id, "Rename Layer", l => l.Name, (l, v) => l.Name = v, name);

    public void SetOpacity(string id, float opacity)
        => Edit(id, "Opacity", l => l.Opacity, (l, v) => l.Opacity = v, Clamp01(opacity));

    public void SetFillOpacity(string id, float opacity)
        => Edit(id, "Fill Opacity", l => l.FillOpacity, (l, v) => l.FillOpacity = v, Clamp01(opacity));

    public void SetBlend(string id, SdkBlendMode mode)
        => Edit(id, "Blend Mode", l => l.BlendMode, (l, v) => l.BlendMode = v, (Sable.Core.BlendMode)(int)mode);

    public void SetVisible(string id, bool visible)
        => Edit(id, visible ? "Show Layer" : "Hide Layer", l => l.Visible, (l, v) => l.Visible = v, visible);

    public string AddPixelLayer(string name, string? parentId = null, int index = -1)
    {
        var doc = _state.ActiveDocument() ?? throw new InvalidOperationException("no active document");
        var parent = parentId is null ? doc.Layers : Require(parentId).Children;
        int at = index < 0 ? parent.Count : index;
        var layer = new PixelLayer(doc.Width, doc.Height, name);
        Submit(new AddLayerCommand(doc, parent, layer, at));
        return _handles.IdFor(layer);
    }

    public void Remove(string id)
    {
        var doc = _state.ActiveDocument() ?? throw new InvalidOperationException("no active document");
        Submit(new RemoveLayerCommand(doc, Require(id)));
    }

    public void Move(string id, int delta)
    {
        var doc = _state.ActiveDocument() ?? throw new InvalidOperationException("no active document");
        Submit(new MoveLayerCommand(doc, Require(id), delta));
    }
}

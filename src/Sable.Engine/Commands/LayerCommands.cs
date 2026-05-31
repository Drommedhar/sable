using System.Linq;
using Sable.Core.Undo;
using Sable.Engine.Layers;

namespace Sable.Engine.Commands;

/// <summary>Insert a layer into a specific parent list at an index. Undo removes it.</summary>
public sealed class AddLayerCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly List<Layer> _parent;
    private readonly Layer _layer;
    private readonly int _index;

    public AddLayerCommand(Document doc, List<Layer> parent, Layer layer, int index)
    {
        _doc = doc;
        _parent = parent;
        _layer = layer;
        _index = Math.Clamp(index, 0, parent.Count);
    }

    public string Name => "Add Layer";

    public void Do() { _parent.Insert(_index, _layer); _doc.MarkStructureChanged(); }
    public void Undo() { _parent.Remove(_layer); _doc.MarkStructureChanged(); }
}

/// <summary>Replace a layer's whole mask (e.g. AI background removal). Undo restores the prior mask.</summary>
public sealed class SetMaskCommand : IUndoableCommand
{
    private readonly Layer _layer;
    private readonly byte[]? _before;
    private readonly byte[]? _after;

    public SetMaskCommand(Layer layer, byte[]? after)
    {
        _layer = layer;
        _before = layer.Mask;
        _after = after;
    }

    public string Name => "Set Mask";
    public void Do() { _layer.Mask = _after; _layer.MaskDirty = true; _layer.Dirty = true; }
    public void Undo() { _layer.Mask = _before; _layer.MaskDirty = true; _layer.Dirty = true; }
}

/// <summary>
/// Replace a set of layers in one parent list with a single new layer (merge-down /
/// flatten / merge-visible / rasterise). Undo restores the removed layers at their
/// original positions and removes the new one. Stamp uses this with no removals.
/// </summary>
public sealed class ReplaceLayersCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly List<Layer> _parent;
    private readonly Layer _newLayer;
    private readonly int _insertIndex;
    private readonly List<(Layer layer, int index)> _removed;

    public ReplaceLayersCommand(Document doc, List<Layer> parent, IEnumerable<Layer> remove, int insertIndex, Layer newLayer, string name = "Merge Layers")
    {
        _doc = doc;
        _parent = parent;
        _newLayer = newLayer;
        _insertIndex = insertIndex;
        Name = name;
        // capture original positions, low→high, so undo can re-insert exactly
        _removed = remove.Select(l => (l, parent.IndexOf(l))).Where(t => t.Item2 >= 0)
                         .OrderBy(t => t.Item2).ToList();
    }

    public string Name { get; }

    public void Do()
    {
        foreach (var (layer, _) in _removed) _parent.Remove(layer);
        _parent.Insert(Math.Clamp(_insertIndex, 0, _parent.Count), _newLayer);
        _doc.MarkStructureChanged();
    }

    public void Undo()
    {
        _parent.Remove(_newLayer);
        foreach (var (layer, index) in _removed)   // already low→high
            _parent.Insert(Math.Clamp(index, 0, _parent.Count), layer);
        _doc.MarkStructureChanged();
    }
}

/// <summary>Remove a layer from wherever it lives. Undo re-inserts at its original spot.</summary>
public sealed class RemoveLayerCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;
    private List<Layer>? _parent;
    private int _index;

    public RemoveLayerCommand(Document doc, Layer layer) { _doc = doc; _layer = layer; }

    public string Name => "Delete Layer";

    public void Do()
    {
        _parent = _doc.FindParent(_layer);
        if (_parent is null) return;
        _index = _parent.IndexOf(_layer);
        _parent.RemoveAt(_index);
        _doc.MarkStructureChanged();
    }

    public void Undo()
    {
        if (_parent is null) return;
        _parent.Insert(Math.Clamp(_index, 0, _parent.Count), _layer);
        _doc.MarkStructureChanged();
    }
}

/// <summary>Move a layer up/down within its parent list (delta = +1 up / -1 down).</summary>
public sealed class MoveLayerCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;
    private readonly int _delta;
    private int _from = -1, _to = -1;

    public MoveLayerCommand(Document doc, Layer layer, int delta) { _doc = doc; _layer = layer; _delta = delta; }

    public string Name => "Reorder Layer";

    public void Do()
    {
        var parent = _doc.FindParent(_layer);
        if (parent is null) return;
        _from = parent.IndexOf(_layer);
        _to = Math.Clamp(_from + _delta, 0, parent.Count - 1);
        if (_from == _to) return;
        parent.RemoveAt(_from);
        parent.Insert(_to, _layer);
        _doc.MarkStructureChanged();
    }

    public void Undo()
    {
        if (_from < 0 || _from == _to) return;
        var parent = _doc.FindParent(_layer);
        if (parent is null) return;
        parent.Remove(_layer);
        parent.Insert(Math.Clamp(_from, 0, parent.Count), _layer);
        _doc.MarkStructureChanged();
    }
}

/// <summary>
/// Wrap one or more layers (sharing a parent) in a new group at the position of the
/// lowest, preserving their relative order. Undo unwraps back to original indices.
/// </summary>
public sealed class GroupLayersCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly GroupLayer _group = new();
    private readonly List<Layer> _layers;          // ordered bottom→top
    private List<Layer>? _parent;
    private List<int> _origIndices = new();        // ascending, matches _layers
    private int _insertAt;

    public GroupLayersCommand(Document doc, IEnumerable<Layer> layers)
    {
        _doc = doc;
        _layers = layers.ToList();
    }

    public string Name => "Group";
    public GroupLayer Group => _group;

    public void Do()
    {
        if (_layers.Count == 0) return;
        _parent = _doc.FindParent(_layers[0]);
        if (_parent is null) return;

        // order by current index (bottom→top); only those actually in this parent
        var ordered = _layers.Where(l => _parent.Contains(l))
                             .OrderBy(l => _parent!.IndexOf(l)).ToList();
        if (ordered.Count == 0) { _parent = null; return; }

        _origIndices = ordered.Select(l => _parent!.IndexOf(l)).ToList();
        _insertAt = _origIndices[0];

        foreach (var l in ordered) _parent.Remove(l);
        if (_group.Children.Count == 0) _group.Children.AddRange(ordered);
        _parent.Insert(Math.Clamp(_insertAt, 0, _parent.Count), _group);
        _doc.MarkStructureChanged();
    }

    public void Undo()
    {
        if (_parent is null) return;
        _parent.Remove(_group);
        var children = _group.Children.ToList();
        for (int k = 0; k < children.Count; k++)
            _parent.Insert(Math.Clamp(_origIndices[k], 0, _parent.Count), children[k]);
        _group.Children.Clear();
        _group.Children.AddRange(children);   // keep for redo
        _doc.MarkStructureChanged();
    }
}

/// <summary>Set a layer's non-destructive position offset (Move tool). Undoable.</summary>
public sealed class MoveOffsetCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;
    private readonly int _oldX, _oldY, _newX, _newY;

    public MoveOffsetCommand(Document doc, Layer layer, int oldX, int oldY, int newX, int newY)
    {
        _doc = doc; _layer = layer;
        _oldX = oldX; _oldY = oldY; _newX = newX; _newY = newY;
    }

    public string Name => "Move";

    public void Do() { _layer.OffsetX = _newX; _layer.OffsetY = _newY; _doc.MarkStructureChanged(); }
    public void Undo() { _layer.OffsetX = _oldX; _layer.OffsetY = _oldY; _doc.MarkStructureChanged(); }
}

/// <summary>Snapshot of a layer's full affine transform (Transform tool). Undoable.</summary>
public readonly record struct LayerXform(int OffsetX, int OffsetY, float ScaleX, float ScaleY, float Rotation, float ShearX, float ShearY,
    bool Perspective = false, float[]? PerspCorners = null)
{
    public static LayerXform From(Layer l) => new(l.OffsetX, l.OffsetY, l.ScaleX, l.ScaleY, l.Rotation, l.ShearX, l.ShearY,
        l.Perspective, l.PerspCorners is { } pc ? (float[])pc.Clone() : null);
    public void ApplyTo(Layer l)
    {
        l.OffsetX = OffsetX; l.OffsetY = OffsetY; l.ScaleX = ScaleX; l.ScaleY = ScaleY; l.Rotation = Rotation; l.ShearX = ShearX; l.ShearY = ShearY;
        l.Perspective = Perspective;
        l.PerspCorners = PerspCorners is { } pc ? (float[])pc.Clone() : null;
    }
}

public sealed class TransformLayerCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;
    private readonly LayerXform _old, _new;

    public TransformLayerCommand(Document doc, Layer layer, LayerXform old, LayerXform @new)
    {
        _doc = doc; _layer = layer; _old = old; _new = @new;
    }

    public string Name => "Transform";
    public void Do() { _new.ApplyTo(_layer); _doc.MarkStructureChanged(); }
    public void Undo() { _old.ApplyTo(_layer); _doc.MarkStructureChanged(); }
}

/// <summary>Move a layer to a target parent list at an index (drag-drop). Undo returns it.</summary>
public sealed class MoveLayerToCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;
    private readonly List<Layer> _target;
    private readonly int _index;
    private List<Layer>? _oldParent;
    private int _oldIndex;

    public MoveLayerToCommand(Document doc, Layer layer, List<Layer> target, int index)
    {
        _doc = doc; _layer = layer; _target = target; _index = index;
    }

    public string Name => "Move Layer";

    public void Do()
    {
        _oldParent = _doc.FindParent(_layer);
        if (_oldParent is null) return;
        _oldIndex = _oldParent.IndexOf(_layer);
        _oldParent.RemoveAt(_oldIndex);
        _target.Insert(Math.Clamp(_index, 0, _target.Count), _layer);
        _doc.MarkStructureChanged();
    }

    public void Undo()
    {
        if (_oldParent is null) return;
        _target.Remove(_layer);
        _oldParent.Insert(Math.Clamp(_oldIndex, 0, _oldParent.Count), _layer);
        _doc.MarkStructureChanged();
    }
}

/// <summary>Dissolve a group, splicing its children into the parent. Undo regroups.</summary>
public sealed class UngroupCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly GroupLayer _group;
    private List<Layer>? _parent;
    private int _index;
    private List<Layer> _children = new();

    public UngroupCommand(Document doc, GroupLayer group) { _doc = doc; _group = group; }

    public string Name => "Ungroup";

    public void Do()
    {
        _parent = _doc.FindParent(_group);
        if (_parent is null) return;
        _index = _parent.IndexOf(_group);
        _children = new List<Layer>(_group.Children);
        _parent.RemoveAt(_index);
        for (int k = 0; k < _children.Count; k++)
            _parent.Insert(_index + k, _children[k]);
        _doc.MarkStructureChanged();
    }

    public void Undo()
    {
        if (_parent is null) return;
        foreach (var c in _children) _parent.Remove(c);
        _parent.Insert(Math.Clamp(_index, 0, _parent.Count), _group);
        _doc.MarkStructureChanged();
    }
}

/// <summary>Snapshot of a vector path's node list (Node tool edits). Undoable. Closed flag
/// can also change (e.g. deleting the closing node). Replaces the whole node list on do/undo.</summary>
public sealed class EditPathCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly PathLayer _path;
    private readonly System.Collections.Generic.List<PathNode> _before, _after;
    private readonly bool _beforeClosed, _afterClosed;

    public EditPathCommand(Document doc, PathLayer path,
        System.Collections.Generic.List<PathNode> before, bool beforeClosed,
        System.Collections.Generic.List<PathNode> after, bool afterClosed)
    {
        _doc = doc; _path = path;
        _before = new System.Collections.Generic.List<PathNode>(before);
        _after = new System.Collections.Generic.List<PathNode>(after);
        _beforeClosed = beforeClosed; _afterClosed = afterClosed;
    }

    public string Name => "Edit Path";

    private void Apply(System.Collections.Generic.List<PathNode> nodes, bool closed)
    {
        _path.Nodes = new System.Collections.Generic.List<PathNode>(nodes);
        _path.Closed = closed;
        _path.Dirty = true;
        _doc.MarkStructureChanged();
    }

    public void Do() => Apply(_after, _afterClosed);
    public void Undo() => Apply(_before, _beforeClosed);
}

/// <summary>Set or clear a text layer's on-path polyline (Fit/Detach Text to Path). Undoable.</summary>
public sealed class SetTextPathCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly TextLayer _text;
    private readonly System.Collections.Generic.List<(float, float)> _before, _after;

    public SetTextPathCommand(Document doc, TextLayer text, System.Collections.Generic.List<(float, float)> after)
    {
        _doc = doc; _text = text;
        _before = new System.Collections.Generic.List<(float, float)>(text.PathPoints);
        _after = new System.Collections.Generic.List<(float, float)>(after);
    }

    public string Name => "Text on Path";

    private void Apply(System.Collections.Generic.List<(float, float)> pts)
    {
        _text.PathPoints = new System.Collections.Generic.List<(float, float)>(pts);
        _text.Dirty = true;
        _doc.MarkStructureChanged();
    }

    public void Do() => Apply(_after);
    public void Undo() => Apply(_before);
}

/// <summary>Batch offset change for align/distribute of several layers. Undoable as one step.</summary>
public sealed class AlignLayersCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly System.Collections.Generic.List<(Layer layer, int ox, int oy, int nx, int ny)> _moves;

    public AlignLayersCommand(Document doc, System.Collections.Generic.List<(Layer, int, int, int, int)> moves)
    {
        _doc = doc; _moves = moves;
    }

    public string Name => "Align";

    public void Do() { foreach (var (l, _, _, nx, ny) in _moves) { l.OffsetX = nx; l.OffsetY = ny; } _doc.MarkStructureChanged(); }
    public void Undo() { foreach (var (l, ox, oy, _, _) in _moves) { l.OffsetX = ox; l.OffsetY = oy; } _doc.MarkStructureChanged(); }
}

/// <summary>Replace the document's entire layer list (History-panel snapshot restore). Undoable.</summary>
public sealed class RestoreSnapshotCommand : IUndoableCommand
{
    private readonly Document _doc;
    private readonly System.Collections.Generic.List<Layer> _before, _after;

    public RestoreSnapshotCommand(Document doc, System.Collections.Generic.List<Layer> after)
    {
        _doc = doc;
        _before = new System.Collections.Generic.List<Layer>(doc.Layers);
        _after = after;
    }

    public string Name => "Restore Snapshot";

    private void Apply(System.Collections.Generic.List<Layer> layers)
    {
        _doc.Layers.Clear();
        foreach (var l in layers) _doc.Layers.Add(l.Clone());   // clone so the stored snapshot stays pristine
        _doc.MarkStructureChanged();
    }

    public void Do() => Apply(_after);
    public void Undo() => Apply(_before);
}

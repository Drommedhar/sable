using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sable.Core;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;

namespace Sable.UI.ViewModels;

/// <summary>A colour-tag swatch (index + brush) for the layer panel picker.</summary>
public sealed record TagSwatch(int Index, Avalonia.Media.IBrush Brush);

/// <summary>
/// MVVM wrapper over a <see cref="Document"/>. Structural edits (add/delete/reorder)
/// go through an <see cref="UndoStack"/>; the layer list is resynced from the engine
/// document whenever the stack changes, so undo/redo stay consistent no matter who
/// triggered the change. Layers are presented top→bottom (Photoshop order).
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject
{
    public Document Model { get; }
    public UndoStack Undo { get; } = new();

    public ObservableCollection<LayerViewModel> Layers { get; } = new();

    public IReadOnlyList<BlendMode> BlendModes { get; } =
        (BlendMode[])Enum.GetValues(typeof(BlendMode));

    /// <summary>Colour-tag swatches for the layer panel (index 0 = none/clear).</summary>
    public IReadOnlyList<TagSwatch> TagSwatches { get; } = Enumerable.Range(0, 8)
        .Select(i => new TagSwatch(i, LayerViewModel.TagBrushFor(i))).ToList();

    [ObservableProperty]
    private LayerViewModel? _selectedLayer;

    private int _newLayerCounter = 1;

    /// <summary>Named full-document snapshots (History panel) — each keeps a deep clone of the layer tree.</summary>
    public List<(string Name, List<Layer> Layers)> Snapshots { get; } = new();

    /// <summary>Capture the current layer tree as a named snapshot.</summary>
    public void CaptureSnapshot(string name)
        => Snapshots.Add((name, Model.Layers.Select(l => l.Clone()).ToList()));

    /// <summary>Restore a snapshot (undoable: swaps the whole layer list).</summary>
    public void RestoreSnapshot(int index)
    {
        if (index < 0 || index >= Snapshots.Count) return;
        // pass the stored snapshot directly — RestoreSnapshotCommand.Apply clones from it on each
        // do/redo, so it stays pristine (no redundant pre-clone here).
        Undo.Execute(new Sable.Engine.Commands.RestoreSnapshotCommand(Model, Snapshots[index].Layers));
    }

    public DocumentViewModel(Document model)
    {
        Model = model;
        Undo.Changed += Resync;
        Resync();
    }

    /// <summary>Rebuild the layer VMs from the engine tree (top→bottom, indented), keeping selection.</summary>
    private void Resync()
    {
        var keepModel = SelectedLayer?.Model;
        Layers.Clear();
        AddTree(Model.Layers, 0);
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == keepModel) ?? Layers.FirstOrDefault();
        // Drop multi-selection refs to layers no longer in the tree (undo/redo/delete can remove them),
        // else Group/DropLayer would act on dangling models.
        var live = new HashSet<Layer>(Layers.Select(vm => vm.Model));
        SelectionModels.RemoveAll(m => !live.Contains(m));
        UndoEditCommand.NotifyCanExecuteChanged();
        RedoEditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Groups collapsed in the panel (transient, in-session). Model refs stay stable across resync.</summary>
    private readonly HashSet<Layer> _collapsed = new();

    // top→bottom: a group row appears above its (indented) children
    private void AddTree(List<Layer> list, int depth, LayerViewModel? parentVm = null)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var layer = list[i];
            bool expanded = !_collapsed.Contains(layer);
            var vm = new LayerViewModel(layer, depth, expanded) { ParentVm = parentVm };
            parentVm?.ChildVms.Add(vm);   // link so toggling a group refreshes its children's eye icons
            Layers.Add(vm);
            // any layer can hold children: a group's content OR a content layer's nested
            // effect layers (live filters / adjustments) — both flatten as indented rows.
            if (layer.HasChildren && expanded) AddTree(layer.Children, depth + 1, vm);
        }
    }

    /// <summary>Toggle a group row's collapsed state and rebuild the flattened list.</summary>
    public void ToggleExpand(LayerViewModel vm)
    {
        if (!vm.Model.HasChildren) return;
        if (!_collapsed.Remove(vm.Model)) _collapsed.Add(vm.Model);
        Resync();
    }

    /// <summary>The list the selected layer lives in (its parent), or the document root.</summary>
    private List<Layer> TargetParent()
        => SelectedLayer is null ? Model.Layers : Model.FindParent(SelectedLayer.Model) ?? Model.Layers;

    private int InsertIndex(List<Layer> parent)
        => SelectedLayer is not null && parent.Contains(SelectedLayer.Model)
            ? parent.IndexOf(SelectedLayer.Model) + 1
            : parent.Count;

    private void AddLayer(Layer layer)
    {
        var parent = TargetParent();
        Undo.Execute(new AddLayerCommand(Model, parent, layer, InsertIndex(parent)));
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == layer);
    }

    [RelayCommand]
    private void NewLayer() => AddLayer(new PixelLayer(Model.Width, Model.Height, $"Layer {_newLayerCounter++}"));

    /// <summary>Select the row whose model is <paramref name="m"/> (no-op if not present).</summary>
    public void SelectModel(Layer m)
    {
        var vm = Layers.FirstOrDefault(v => v.Model == m);
        if (vm is not null) SelectedLayer = vm;
    }

    /// <summary>Add a pre-built layer (e.g. a drawn shape) at the top and select it. Undoable.</summary>
    public void AddAndSelect(Layer layer)
    {
        Undo.Execute(new AddLayerCommand(Model, Model.Layers, layer, Model.Layers.Count));
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == layer);
    }

    /// <summary>Duplicate the selected layer (Ctrl+J): a deep clone inserted just above it. Undoable.</summary>
    [RelayCommand]
    private void DuplicateLayer()
    {
        if (SelectedLayer is null) return;
        var clone = SelectedLayer.Model.Clone();
        clone.Name = SelectedLayer.Model.Name + " copy";
        var parent = TargetParent();
        Undo.Execute(new AddLayerCommand(Model, parent, clone, InsertIndex(parent)));
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == clone);
    }

    /// <summary>Add a pre-built layer at the top of the document and select it. Undoable (used by Paste).</summary>
    public void PasteLayer(Layer layer)
    {
        Undo.Execute(new AddLayerCommand(Model, Model.Layers, layer, Model.Layers.Count));
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == layer);
    }

    [RelayCommand]
    private void NewAdjustment(AdjustmentKind kind) => AddEffectLayer(new AdjustmentLayer(kind));

    [RelayCommand]
    private void NewFilter(FilterKind kind) => AddEffectLayer(new FilterLayer(kind));

    /// <summary>
    /// Add a live filter / adjustment. Affinity model: if a content layer (pixel/shape/
    /// text/path) is selected, the effect NESTS inside it (clipped to that layer only);
    /// otherwise it goes in as a sibling and affects the whole composite below it.
    /// </summary>
    private void AddEffectLayer(Layer effect)
    {
        if (SelectedLayer?.Model is PixelLayer or ShapeLayer or TextLayer or PathLayer)
        {
            var host = SelectedLayer.Model;
            Undo.Execute(new AddLayerCommand(Model, host.Children, effect, host.Children.Count));
        }
        else
        {
            var parent = TargetParent();
            Undo.Execute(new AddLayerCommand(Model, parent, effect, InsertIndex(parent)));
        }
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == effect);
    }

    [RelayCommand]
    private void DeleteLayer()
    {
        if (SelectedLayer is null) return;
        Undo.Execute(new RemoveLayerCommand(Model, SelectedLayer.Model));
    }

    [RelayCommand]
    private void MoveLayerUp()
    {
        if (SelectedLayer is null) return;
        Undo.Execute(new MoveLayerCommand(Model, SelectedLayer.Model, +1));
    }

    [RelayCommand]
    private void MoveLayerDown()
    {
        if (SelectedLayer is null) return;
        Undo.Execute(new MoveLayerCommand(Model, SelectedLayer.Model, -1));
    }

    /// <summary>Models currently multi-selected in the panel (set from the view).</summary>
    public List<Layer> SelectionModels { get; } = new();

    public void SetSelection(IEnumerable<LayerViewModel> selected)
    {
        SelectionModels.Clear();
        SelectionModels.AddRange(selected.Select(v => v.Model));
    }

    [RelayCommand]
    private void Group()
    {
        // group the multi-selection if any, else the single selected layer
        var targets = SelectionModels.Count > 0
            ? SelectionModels.ToList()
            : (SelectedLayer is null ? new List<Layer>() : new() { SelectedLayer.Model });
        if (targets.Count == 0) return;

        var cmd = new GroupLayersCommand(Model, targets);
        Undo.Execute(cmd);
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == cmd.Group);
    }

    [RelayCommand]
    private void Ungroup()
    {
        if (SelectedLayer?.Model is GroupLayer g)
            Undo.Execute(new UngroupCommand(Model, g));
    }

    /// <summary>Add a white raster mask to the selected layer, or remove it if it already has one.</summary>
    [RelayCommand]
    private void ToggleMask()
    {
        if (SelectedLayer?.Model is not { } m) return;
        byte[]? after = null;
        if (!m.HasMask)
        {
            // mask is layer-aligned: a pixel layer's mask matches its buffer, others use doc size.
            int mw = m is PixelLayer px ? px.Width : Model.Width;
            int mh = m is PixelLayer py ? py.Height : Model.Height;
            after = new byte[mw * mh * 4];
            Array.Fill(after, (byte)255);
        }
        Undo.Execute(new SetMaskCommand(m, after));   // undoable (Resync runs on Undo.Changed)
        SelectedLayer?.RaiseMaskChanged();
    }

    /// <summary>
    /// Drag-drop ONTO a target row (middle band). Decides the action by what's dragged:
    /// onto a group → move all in; a live filter / adjustment onto a content layer → NEST it
    /// as a child (clipped to that layer, Affinity model); otherwise → auto-group target +
    /// dragged into a new group. Supports a multi-selection drag.
    /// </summary>
    public void DropOnto(IReadOnlyList<Layer> dragged, Layer target)
    {
        var items = dragged
            .Where(d => !ReferenceEquals(d, target) && !(d is GroupLayer dg && Contains(dg, target)))
            .ToList();
        if (items.Count == 0) return;

        // 1. onto a group → move all into it
        if (target is GroupLayer)
        {
            MoveAllInto(items, target.Children);
            return;
        }

        // 2. all-effect drag onto a content layer → nest as children (clip to that layer)
        bool allEffects = items.All(d => d is FilterLayer or AdjustmentLayer);
        bool targetIsContent = target is PixelLayer or ShapeLayer or TextLayer or PathLayer;
        if (allEffects && targetIsContent)
        {
            MoveAllInto(items, target.Children);
            return;
        }

        // 3. otherwise auto-group target + dragged
        AutoGroup(items, target);
    }

    // move each item into a destination list (top), preserving drag order; select the last.
    private void MoveAllInto(List<Layer> items, List<Layer> dest)
    {
        foreach (var d in items) Undo.Execute(new MoveLayerToCommand(Model, d, dest, dest.Count));
        SelectModel(items[^1]);
    }

    private void AutoGroup(List<Layer> items, Layer target)
    {
        var parent = Model.FindParent(target);
        if (parent is null) return;
        // bring any dragged from other parents next to the target first, so they share a parent
        foreach (var d in items.Where(d => !ReferenceEquals(Model.FindParent(d), parent)).ToList())
            Undo.Execute(new MoveLayerToCommand(Model, d, parent, parent.IndexOf(target)));
        var groupSet = new List<Layer>(items);
        if (!groupSet.Contains(target)) groupSet.Add(target);
        var cmd = new GroupLayersCommand(Model, groupSet);
        Undo.Execute(cmd);
        SelectModel(cmd.Group);
    }

    /// <summary>Reorder a multi-selection above/below the target (between-row drop).</summary>
    public void DropMultipleRelative(IReadOnlyList<Layer> dragged, Layer target, bool above)
    {
        if (dragged.Count == 1) { DropLayerRelative(dragged[0], target, above); return; }
        var parent = Model.FindParent(target) ?? Model.Layers;
        var items = dragged
            .Where(d => !ReferenceEquals(d, target) && !(d is GroupLayer dg && Contains(dg, target)))
            .ToList();
        if (items.Count == 0) return;
        int ti = parent.IndexOf(target);
        int insert = above ? ti + 1 : ti;
        foreach (var d in items)
        {
            Undo.Execute(new MoveLayerToCommand(Model, d, parent, Math.Clamp(insert, 0, parent.Count)));
            insert = parent.IndexOf(d) + 1;   // stack the next one just above the one we placed
        }
        SelectModel(items[^1]);
    }

    /// <summary>Drop <paramref name="dragged"/> just above/below <paramref name="target"/> (between-row reorder).
    /// UI is top→bottom; "above" = toward the top = a higher index in the bottom→top model list.</summary>
    public void DropLayerRelative(Layer dragged, Layer target, bool above)
    {
        if (dragged == target) return;
        if (dragged is GroupLayer dg && Contains(dg, target)) return;   // no cycles
        var parent = Model.FindParent(target) ?? Model.Layers;
        int ti = parent.IndexOf(target);
        int insert = above ? ti + 1 : ti;
        if (ReferenceEquals(Model.FindParent(dragged), parent))
        {
            int di = parent.IndexOf(dragged);
            if (di < insert) insert--;       // MoveLayerToCommand removes first → shift insert down
            if (di == insert) return;        // no-op (already there)
        }
        Undo.Execute(new MoveLayerToCommand(Model, dragged, parent, insert));
        SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == dragged);
    }

    private static bool Contains(GroupLayer group, Layer layer)
    {
        foreach (var c in group.Children)
        {
            if (c == layer) return true;
            if (c is GroupLayer g && Contains(g, layer)) return true;
        }
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoEdit() => Undo.Undo();
    private bool CanUndo() => Undo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void RedoEdit() => Undo.Redo();
    private bool CanRedo() => Undo.CanRedo;
}

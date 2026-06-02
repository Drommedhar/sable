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
        var copy = Snapshots[index].Layers.Select(l => l.Clone()).ToList();
        Undo.Execute(new Sable.Engine.Commands.RestoreSnapshotCommand(Model, copy));
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
    private void AddTree(List<Layer> list, int depth)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            bool expanded = list[i] is not GroupLayer g0 || !_collapsed.Contains(g0);
            Layers.Add(new LayerViewModel(list[i], depth, expanded));
            if (list[i] is GroupLayer g && expanded) AddTree(g.Children, depth + 1);
        }
    }

    /// <summary>Toggle a group row's collapsed state and rebuild the flattened list.</summary>
    public void ToggleExpand(LayerViewModel vm)
    {
        if (vm.Model is not GroupLayer g) return;
        if (!_collapsed.Remove(g)) _collapsed.Add(g);
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
    private void NewAdjustment(AdjustmentKind kind) => AddLayer(new AdjustmentLayer(kind));

    [RelayCommand]
    private void NewFilter(FilterKind kind) => AddLayer(new FilterLayer(kind));

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
    /// Drag-drop: drop <paramref name="dragged"/> onto <paramref name="target"/>.
    /// Onto a group → move into it; onto a sibling layer → auto-group the two;
    /// otherwise reorder into the target's parent.
    /// </summary>
    public void DropLayer(Layer dragged, Layer target)
    {
        if (dragged == target) return;
        if (dragged is GroupLayer dg && Contains(dg, target)) return;   // no cycles

        if (target is GroupLayer g)
        {
            Undo.Execute(new MoveLayerToCommand(Model, dragged, g.Children, g.Children.Count));
            SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == dragged);
            return;
        }

        var pT = Model.FindParent(target);
        var pD = Model.FindParent(dragged);
        if (pT is null || pD is null) return;

        if (ReferenceEquals(pT, pD))
        {
            // auto-group the two sibling layers
            var cmd = new GroupLayersCommand(Model, new[] { dragged, target });
            Undo.Execute(cmd);
            SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == cmd.Group);
        }
        else
        {
            Undo.Execute(new MoveLayerToCommand(Model, dragged, pT, pT.IndexOf(target)));
            SelectedLayer = Layers.FirstOrDefault(vm => vm.Model == dragged);
        }
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

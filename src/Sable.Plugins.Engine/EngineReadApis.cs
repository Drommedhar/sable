using System;
using System.Collections.Generic;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Document;
using Sable.Plugin.Sdk.Layers;

namespace Sable.Plugins.Engine;

/// <summary>Shared accessors the engine-backed APIs read through, so the app can point them at the
/// active tab's document/undo/selection without these adapters knowing about tabs or Avalonia.</summary>
public sealed record EngineHostState
{
    public required Func<Document?> ActiveDocument { get; init; }
    public required Func<Sable.Core.Undo.UndoStack?> ActiveUndo { get; init; }

    /// <summary>The single selected layer, or null (none / multi-selection).</summary>
    public Func<Layer?>? SelectedLayer { get; init; }

    /// <summary>The flattened composite (RGBA8 + size), or null when unavailable (headless / no GPU).
    /// Supplied by the app (GPU readback); the engine bridge can't composite on its own.</summary>
    public Func<(byte[] Rgba, int Width, int Height)?>? ReadComposite { get; init; }
}

/// <summary>Read access to the active document (capability <c>document.read</c>).</summary>
public sealed class EngineDocumentApi : IDocumentApi
{
    private readonly EngineHostState _state;
    public EngineDocumentApi(EngineHostState state) => _state = state;

    public DocumentInfo? Active
    {
        get
        {
            if (_state.ActiveDocument() is not { } d) return null;
            var sel = d.Selection;
            return new DocumentInfo
            {
                Width = d.Width,
                Height = d.Height,
                Dpi = d.Dpi,
                Depth = ((int)d.Depth).ToString(),
                LayerCount = d.Layers.Count,
                IccProfileName = d.IccProfileName,
                HasSelection = sel is not null || d.SelectionMask is not null,
                SelectionX = sel?.X ?? 0,
                SelectionY = sel?.Y ?? 0,
                SelectionWidth = sel?.W ?? 0,
                SelectionHeight = sel?.H ?? 0,
            };
        }
    }
}

/// <summary>Read access to the active document's layers (capability <c>layer.read</c>).</summary>
public sealed class EngineLayerApi : ILayerApi
{
    private readonly EngineHostState _state;
    private readonly LayerHandles _handles;

    public EngineLayerApi(EngineHostState state, LayerHandles handles)
    {
        _state = state;
        _handles = handles;
    }

    public static string KindOf(Layer l) => l switch
    {
        GroupLayer => "group",
        AdjustmentLayer => "adjustment",
        FilterLayer => "filter",
        ShapeLayer => "shape",
        TextLayer => "text",
        PathLayer => "path",
        _ => "pixel",
    };

    public IReadOnlyList<LayerInfo> All()
    {
        var list = new List<LayerInfo>();
        if (_state.ActiveDocument() is { } d)
            Flatten(d, d.Layers, null, list);
        return list;
    }

    public LayerInfo? Selected()
        => _state.SelectedLayer?.Invoke() is { } l && _state.ActiveDocument() is { } d ? ToInfo(d, l, ParentIdOf(d, l)) : null;

    public LayerInfo? ById(string id)
        => _handles.Resolve(id) is { } l && _state.ActiveDocument() is { } d ? ToInfo(d, l, ParentIdOf(d, l)) : null;

    private void Flatten(Document d, IReadOnlyList<Layer> layers, string? parentId, List<LayerInfo> acc)
    {
        foreach (var l in layers)
        {
            acc.Add(ToInfo(d, l, parentId));
            if (l.Children.Count > 0) Flatten(d, l.Children, _handles.IdFor(l), acc);
        }
    }

    // Null for a top-level layer; the owning layer's id when nested.
    private string? ParentIdOf(Document d, Layer l) => FindOwnerId(d.Layers, l);

    // Walk the tree to find which layer owns `target` as a child (for ParentId).
    private string? FindOwnerId(IReadOnlyList<Layer> layers, Layer target)
    {
        foreach (var l in layers)
        {
            if (l.Children.Contains(target)) return _handles.IdFor(l);
            var nested = FindOwnerId(l.Children, target);
            if (nested is not null) return nested;
        }
        return null;
    }

    private LayerInfo ToInfo(Document d, Layer l, string? parentId)
    {
        var (bx, by, bw, bh) = l.ContentBounds(d.Width, d.Height);
        var childIds = new List<string>(l.Children.Count);
        foreach (var c in l.Children) childIds.Add(_handles.IdFor(c));

        return new LayerInfo
        {
            Id = _handles.IdFor(l),
            Name = l.Name,
            Kind = KindOf(l),
            Opacity = l.Opacity,
            FillOpacity = l.FillOpacity,
            Blend = (SdkBlendMode)(int)l.BlendMode,
            Visible = l.Visible,
            Clipped = l.ClipToBelow,
            LockPosition = l.LockPosition,
            LockPixels = l.LockPixels,
            LockAlpha = l.LockAlpha,
            ColorTag = l.ColorTag,
            OffsetX = l.OffsetX,
            OffsetY = l.OffsetY,
            HasMask = l.Mask is not null,
            HasEffects = l.Effects.Count > 0,
            ParentId = parentId,
            ChildIds = childIds,
            BoundsX = bx,
            BoundsY = by,
            BoundsWidth = bw,
            BoundsHeight = bh,
        };
    }
}

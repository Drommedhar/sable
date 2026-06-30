using System;
using System.Collections.Generic;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Pixels;
using Sable.Plugin.Sdk.Selection;

namespace Sable.Plugins.Engine;

/// <summary>Shared transaction state: while <see cref="Pending"/> is non-null, the layer-write API
/// buffers its commands here instead of executing them, and <see cref="EngineTransactionApi"/>
/// commits them as one undo step. Single-threaded (UI thread); not reentrant beyond flattening.</summary>
public sealed class PluginTransaction
{
    internal List<IUndoableCommand>? Pending;
    public bool Active => Pending is not null;
}

/// <summary>Read access to the active selection (capability <c>selection.read</c>).</summary>
public sealed class EngineSelectionApi : ISelectionApi
{
    private readonly EngineHostState _state;
    public EngineSelectionApi(EngineHostState state) => _state = state;

    public SelectionInfo? Current
    {
        get
        {
            if (_state.ActiveDocument() is not { } d) return null;
            var mask = d.SelectionMask;
            SelRect? rect = d.Selection ?? (mask is not null ? Selections.Bounds(mask, d.Width, d.Height) : null);
            if (rect is null && mask is null)
                return new SelectionInfo { HasSelection = false };

            var r = rect ?? new SelRect(0, 0, 0, 0);
            return new SelectionInfo
            {
                HasSelection = true,
                X = r.X, Y = r.Y, Width = r.W, Height = r.H,
                Mask = mask is null ? null : (byte[])mask.Clone(),
            };
        }
    }
}

/// <summary>Read access to pixels (capability <c>pixel.read</c>). Buffers are copies.</summary>
public sealed class EnginePixelApi : IPixelApi
{
    private readonly EngineHostState _state;
    public EnginePixelApi(EngineHostState state) => _state = state;

    public PixelBuffer? ActiveLayer()
    {
        if (_state.SelectedLayer?.Invoke() is PixelLayer px)
            return new PixelBuffer { Width = px.Width, Height = px.Height, Rgba = px.ToBytes() };
        return null;
    }

    public PixelBuffer? Composite()
    {
        if (_state.ReadComposite?.Invoke() is { } c)
            return new PixelBuffer { Width = c.Width, Height = c.Height, Rgba = c.Rgba };
        return null;
    }
}

/// <summary>Groups buffered layer-write commands into ONE undo step (capability <c>undo.transaction</c>).</summary>
public sealed class EngineTransactionApi : ITransactionApi
{
    private readonly EngineHostState _state;
    private readonly PluginTransaction _txn;

    public EngineTransactionApi(EngineHostState state, PluginTransaction txn)
    {
        _state = state;
        _txn = txn;
    }

    public void Run(string name, Action body)
    {
        if (body is null) return;
        if (_txn.Pending is not null) { body(); return; }   // nested → flatten into the outer batch

        var pending = new List<IUndoableCommand>();
        _txn.Pending = pending;
        try { body(); }
        finally { _txn.Pending = null; }

        if (pending.Count > 0) _state.ActiveUndo()?.ExecuteMacro(name, pending);
    }
}

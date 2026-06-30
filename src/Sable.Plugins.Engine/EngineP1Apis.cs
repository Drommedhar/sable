using System;
using System.Collections.Generic;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Pixels;
using Sable.Plugin.Sdk.Selection;
using Sable.Tools;

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

/// <summary>Write access to the active pixel layer (capability <c>pixel.write.layer_output</c>).
/// Every write is a whole-raster snapshot (<see cref="RasterStateCommand"/>) so it's undoable +
/// dirty-tracked; respects an open <see cref="PluginTransaction"/> so a batch is one undo step.</summary>
public sealed class EnginePixelWriteApi : IPixelWriteApi
{
    private readonly EngineHostState _state;
    private readonly PluginTransaction? _txn;

    public EnginePixelWriteApi(EngineHostState state, PluginTransaction? txn = null)
    {
        _state = state;
        _txn = txn;
    }

    private PixelLayer? Active() => _state.SelectedLayer?.Invoke() as PixelLayer;

    private static void Validate(PixelBuffer b)
    {
        if (b is null) throw new ArgumentNullException(nameof(b));
        if (b.Width < 0 || b.Height < 0 || b.Rgba is null || b.Rgba.Length != b.Width * b.Height * 4)
            throw new ArgumentException("pixel buffer length must equal Width*Height*4", nameof(b));
    }

    /// <summary>Snapshot the layer's post-edit raster + record an undoable command (immediate, or
    /// buffered into the open transaction). The edit is already applied to the layer when called.</summary>
    private void Record(PixelLayer px, RasterState before)
    {
        var cmd = new RasterStateCommand(px, before, RasterState.Capture(px), () => px.Dirty = true);
        if (_txn?.Pending is { } pending) pending.Add(cmd);
        else
        {
            var undo = _state.ActiveUndo();
            if (undo is not null) undo.Execute(cmd);   // re-applies `after` (idempotent) + marks dirty
            else px.Dirty = true;                        // no undo stack (headless) → at least repaint
        }
    }

    public bool SetActiveLayerPixels(PixelBuffer buffer)
    {
        Validate(buffer);
        if (Active() is not { } px) return false;
        var before = RasterState.Capture(px);
        px.SetBufferFromBytes(buffer.Width, buffer.Height, buffer.Rgba);
        Record(px, before);
        return true;
    }

    public bool WriteRegion(int x, int y, PixelBuffer buffer)
    {
        Validate(buffer);
        if (Active() is not { } px) return false;
        var before = RasterState.Capture(px);

        // Clip the source rect to the layer; copy straight-alpha RGBA8 (→ float) row by row.
        int sx0 = Math.Max(0, -x), sy0 = Math.Max(0, -y);
        int sx1 = Math.Min(buffer.Width, px.Width - x);
        int sy1 = Math.Min(buffer.Height, px.Height - y);
        if (sx1 > sx0 && sy1 > sy0)
        {
            var dst = px.Pixels;
            for (int sy = sy0; sy < sy1; sy++)
            {
                int dRow = ((y + sy) * px.Width + (x + sx0)) * 4;
                int sRow = (sy * buffer.Width + sx0) * 4;
                for (int i = 0, n = (sx1 - sx0) * 4; i < n; i++)
                    dst[dRow + i] = buffer.Rgba[sRow + i] / 255f;
            }
            px.Dirty = true;
            px.DirtyTiles.Clear();   // no partial-tile info → full re-upload next composite
        }

        Record(px, before);
        return true;
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

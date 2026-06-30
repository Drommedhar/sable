using System;
using System.Collections.Generic;
using System.Threading;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Pixels;
using Sable.Plugins;
using Sable.Plugins.Engine;

namespace Sable.Tests;

/// <summary>Engine-backed plugin host APIs (document/layer read + basic writes + built-in exporters).</summary>
public sealed class EngineHostApiTests
{
    private static (Document doc, UndoStack undo, EngineHostState state, LayerHandles handles) World()
    {
        var doc = new Document(64, 48);
        var undo = new UndoStack();
        var state = new EngineHostState
        {
            ActiveDocument = () => doc,
            ActiveUndo = () => undo,
            SelectedLayer = () => doc.Layers.Count > 0 ? doc.Layers[0] : null,
        };
        return (doc, undo, state, new LayerHandles());
    }

    [Fact]
    public void DocumentApi_reports_active_document()
    {
        var (doc, _, state, _) = World();
        doc.Layers.Add(new PixelLayer(64, 48, "bg"));
        var info = new EngineDocumentApi(state).Active!;
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(1, info.LayerCount);
        Assert.False(info.HasSelection);
    }

    [Fact]
    public void DocumentApi_null_when_no_document()
    {
        var state = new EngineHostState { ActiveDocument = () => null, ActiveUndo = () => null };
        Assert.Null(new EngineDocumentApi(state).Active);
    }

    [Fact]
    public void LayerApi_flattens_tree_with_kinds_and_parent_ids()
    {
        var (doc, _, state, handles) = World();
        var group = new GroupLayer("grp");
        var child = new PixelLayer(64, 48, "inside");
        group.Children.Add(child);
        var adj = new AdjustmentLayer();
        doc.Layers.Add(group);
        doc.Layers.Add(adj);

        var api = new EngineLayerApi(state, handles);
        var all = api.All();
        Assert.Equal(3, all.Count);   // group, child, adjustment

        var g = all.Single(i => i.Name == "grp");
        Assert.Equal("group", g.Kind);
        Assert.Null(g.ParentId);
        Assert.Single(g.ChildIds);

        var c = all.Single(i => i.Name == "inside");
        Assert.Equal("pixel", c.Kind);
        Assert.Equal(g.Id, c.ParentId);

        Assert.Equal("adjustment", all.Single(i => i.Kind == "adjustment").Kind);
        Assert.Equal(g.Id, api.ById(g.Id)!.Id);   // id resolves back
    }

    [Fact]
    public void WriteApi_SetOpacity_is_one_undoable_step()
    {
        var (doc, undo, state, handles) = World();
        var l = new PixelLayer(64, 48, "x") { Opacity = 1f };
        doc.Layers.Add(l);
        var id = new EngineLayerApi(state, handles).All()[0].Id;

        var w = new EngineLayerWriteApi(state, handles);
        w.SetOpacity(id, 0.25f);
        Assert.Equal(0.25f, l.Opacity, 3);
        Assert.Equal(1, undo.Cursor);

        undo.Undo();
        Assert.Equal(1f, l.Opacity, 3);
    }

    [Fact]
    public void WriteApi_clamps_opacity_and_maps_blend()
    {
        var (doc, _, state, handles) = World();
        var l = new PixelLayer(64, 48, "x");
        doc.Layers.Add(l);
        var id = new EngineLayerApi(state, handles).All()[0].Id;
        var w = new EngineLayerWriteApi(state, handles);

        w.SetOpacity(id, 5f);
        Assert.Equal(1f, l.Opacity, 3);
        w.SetBlend(id, (SdkBlendMode)(int)Sable.Core.BlendMode.Multiply);
        Assert.Equal(Sable.Core.BlendMode.Multiply, l.BlendMode);
    }

    [Fact]
    public void WriteApi_AddPixelLayer_then_undo()
    {
        var (doc, undo, state, handles) = World();
        var w = new EngineLayerWriteApi(state, handles);
        var id = w.AddPixelLayer("new");
        Assert.Single(doc.Layers);
        Assert.NotNull(new EngineLayerApi(state, handles).ById(id));
        undo.Undo();
        Assert.Empty(doc.Layers);
    }

    [Fact]
    public void WriteApi_unknown_id_throws()
    {
        var (_, _, state, handles) = World();
        Assert.Throws<ArgumentException>(() => new EngineLayerWriteApi(state, handles).SetVisible("nope", false));
    }

    private sealed class SyncProgress : IProgress<(double, string?)>
    {
        public readonly List<(double Fraction, string? Status)> Items = new();
        public void Report((double, string?) value) => Items.Add(value);
    }

    [Fact]
    public void BatchContext_routes_open_save_close_and_progress()
    {
        var docA = new Document(2, 2);
        Document? active = null;
        var saved = new List<(Document Doc, string Path)>();
        var prog = new SyncProgress();

        var ctx = new BatchContext(
            new[] { "a.png" }, CancellationToken.None,
            open: p => p == "a.png" ? docA : null,
            save: (d, p) => { saved.Add((d, p)); return true; },
            setActive: d => active = d,
            progress: prog);

        Assert.Equal(new[] { "a.png" }, ctx.InputFiles);
        Assert.False(ctx.SaveDocument("x.png"));    // nothing open yet
        Assert.True(ctx.OpenDocument("a.png"));
        Assert.Same(docA, active);                  // opening makes it the host's active document
        Assert.True(ctx.SaveDocument("out.png"));
        Assert.Equal((docA, "out.png"), saved[0]);
        ctx.CloseDocument();
        Assert.Null(active);                        // close clears the active document
        Assert.False(ctx.OpenDocument("missing"));  // loader returned null → false

        ctx.Report(0.5, "half");
        Assert.Contains((0.5, "half"), prog.Items);
    }

    [Fact]
    public void BuiltInExporters_register_four_formats()
    {
        var reg = new ExportRegistry();
        BuiltInExporters.RegisterAll(reg);
        Assert.Equal(4, reg.Providers.Count);
        Assert.True(reg.ById("png")!.SupportsAlpha);
        Assert.False(reg.ById("jpeg")!.SupportsAlpha);
        Assert.Equal("tiff", reg.ByExtension("tif") is null ? reg.ByExtension("tiff")!.Id : reg.ByExtension("tif")!.Id);
    }

    // --- P1 capabilities ---

    [Fact]
    public void SelectionApi_reports_rect_and_mask()
    {
        var (doc, _, state, _) = World();
        Assert.True(new EngineSelectionApi(state).Current is { HasSelection: false });

        doc.Selection = new SelRect(2, 3, 4, 5);
        var info = new EngineSelectionApi(state).Current!;
        Assert.True(info.HasSelection);
        Assert.Equal((2, 3, 4, 5), (info.X, info.Y, info.Width, info.Height));
    }

    [Fact]
    public void PixelApi_reads_active_layer_and_composite()
    {
        var (doc, _, state, _) = World();
        doc.Layers.Add(new PixelLayer(64, 48, "bg"));
        var active = new EnginePixelApi(state).ActiveLayer()!;
        Assert.Equal(64 * 48 * 4, active.Rgba.Length);

        // composite comes from the injected delegate (the app's GPU readback)
        var withComposite = state with { ReadComposite = () => (new byte[8 * 8 * 4], 8, 8) };
        var comp = new EnginePixelApi(withComposite).Composite()!;
        Assert.Equal(8, comp.Width);
        Assert.Null(new EnginePixelApi(state).Composite());   // none injected → null
    }

    [Fact]
    public void PixelWriteApi_replaces_active_layer_and_is_undoable()
    {
        var (doc, undo, state, _) = World();
        var l = new PixelLayer(4, 4, "x");
        doc.Layers.Add(l);

        var rgba = new byte[2 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = 200;
        var ok = new EnginePixelWriteApi(state).SetActiveLayerPixels(
            new PixelBuffer { Width = 2, Height = 2, Rgba = rgba });

        Assert.True(ok);
        Assert.Equal(2, l.Width);
        Assert.Equal(2, l.Height);
        Assert.Equal(200 / 255f, l.Pixels[0], 4);
        Assert.Equal(1, undo.Cursor);

        undo.Undo();
        Assert.Equal(4, l.Width);              // restored to original size
        Assert.Equal(0f, l.Pixels[0], 4);
    }

    [Fact]
    public void PixelWriteApi_writes_clipped_region()
    {
        var (doc, _, state, _) = World();
        var l = new PixelLayer(4, 4, "x");
        doc.Layers.Add(l);

        var red = new byte[2 * 2 * 4];
        for (int p = 0; p < 4; p++) { red[p * 4] = 255; red[p * 4 + 3] = 255; }
        // place at (3,3) so only the top-left source pixel lands inside the 4x4 layer
        var ok = new EnginePixelWriteApi(state).WriteRegion(3, 3, new PixelBuffer { Width = 2, Height = 2, Rgba = red });

        Assert.True(ok);
        int idx = (3 * 4 + 3) * 4;             // layer pixel (3,3)
        Assert.Equal(1f, l.Pixels[idx], 4);    // red
        Assert.Equal(1f, l.Pixels[idx + 3], 4);// alpha
        Assert.Equal(0f, l.Pixels[0], 4);      // untouched elsewhere
    }

    [Fact]
    public void PixelWriteApi_no_active_pixel_layer_returns_false()
    {
        var (doc, _, state, _) = World();
        doc.Layers.Add(new AdjustmentLayer());   // selected layer is not a pixel layer
        var ok = new EnginePixelWriteApi(state).SetActiveLayerPixels(
            new PixelBuffer { Width = 1, Height = 1, Rgba = new byte[4] });
        Assert.False(ok);
    }

    [Fact]
    public void PixelWriteApi_bad_buffer_length_throws()
    {
        var (doc, _, state, _) = World();
        doc.Layers.Add(new PixelLayer(2, 2, "x"));
        Assert.Throws<ArgumentException>(() => new EnginePixelWriteApi(state).SetActiveLayerPixels(
            new PixelBuffer { Width = 2, Height = 2, Rgba = new byte[3] }));
    }

    [Fact]
    public void PixelWriteApi_joins_open_transaction()
    {
        var (doc, undo, state, _) = World();
        var l = new PixelLayer(2, 2, "x");
        doc.Layers.Add(l);

        var txn = new PluginTransaction();
        var w = new EnginePixelWriteApi(state, txn);
        var tx = new EngineTransactionApi(state, txn);

        tx.Run("Plugin paint", () =>
        {
            w.WriteRegion(0, 0, new PixelBuffer { Width = 1, Height = 1, Rgba = new byte[] { 255, 0, 0, 255 } });
            w.WriteRegion(1, 1, new PixelBuffer { Width = 1, Height = 1, Rgba = new byte[] { 0, 255, 0, 255 } });
        });

        Assert.Equal(1, undo.Cursor);          // two writes → ONE undo entry
        Assert.Equal(1f, l.Pixels[0], 4);      // red at (0,0)
        Assert.Equal(1f, l.Pixels[(1 * 2 + 1) * 4 + 1], 4); // green at (1,1)
    }

    [Fact]
    public void TransactionApi_groups_writes_into_one_undo_step()
    {
        var (doc, undo, state, handles) = World();
        var l = new PixelLayer(64, 48, "x") { Opacity = 1f, Visible = true };
        doc.Layers.Add(l);
        var id = new EngineLayerApi(state, handles).All()[0].Id;

        var txn = new PluginTransaction();
        var w = new EngineLayerWriteApi(state, handles, txn);
        var tx = new EngineTransactionApi(state, txn);

        tx.Run("Batch edit", () => { w.SetOpacity(id, 0.5f); w.SetVisible(id, false); });

        Assert.Equal(1, undo.Cursor);          // two writes → ONE history entry
        Assert.Equal(0.5f, l.Opacity, 3);
        Assert.False(l.Visible);

        undo.Undo();                            // reverts the whole batch
        Assert.Equal(1f, l.Opacity, 3);
        Assert.True(l.Visible);
    }

    [Fact]
    public void Writes_outside_a_transaction_are_separate_steps()
    {
        var (doc, undo, state, handles) = World();
        doc.Layers.Add(new PixelLayer(64, 48, "x"));
        var id = new EngineLayerApi(state, handles).All()[0].Id;
        var w = new EngineLayerWriteApi(state, handles, new PluginTransaction());
        w.SetOpacity(id, 0.5f);
        w.SetVisible(id, false);
        Assert.Equal(2, undo.Cursor);
    }
}

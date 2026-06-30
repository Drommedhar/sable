using System;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Plugin.Sdk;
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

using System.Linq;
using Sable.Core.Settings;

namespace Sable.Tests;

/// <summary>Shortcut migration presets (ROADMAP P3).</summary>
public sealed class KeymapPresetTests
{
    [Fact]
    public void Sable_preset_equals_catalog_defaults()
    {
        foreach (var c in KeyCommands.Catalog)
            Assert.Equal(c.DefaultGesture, KeymapPresets.GestureFor(KeymapPresets.Sable, c.Id));
    }

    [Fact]
    public void Photoshop_overrides_redo_and_new_layer()
    {
        Assert.Equal("Ctrl+Shift+Z", KeymapPresets.GestureFor(KeymapPresets.Photoshop, "edit.redo"));
        Assert.Equal("Ctrl+Shift+N", KeymapPresets.GestureFor(KeymapPresets.Photoshop, "layer.new"));
        // unrelated command falls through to the catalog default
        Assert.Equal("Ctrl+S", KeymapPresets.GestureFor(KeymapPresets.Photoshop, "file.save"));
    }

    [Fact]
    public void Apply_writes_only_non_default_overrides_into_settings()
    {
        var s = new SableSettings();
        KeymapPresets.Apply(s, KeymapPresets.Photoshop);

        // redo differs from default → stored; effective gesture reflects it
        Assert.Equal("Ctrl+Shift+Z", s.KeyBindings["edit.redo"]);
        Assert.Equal("Ctrl+Shift+Z", s.GestureFor("edit.redo"));
        // file.save matches default → NOT stored, but still resolves via GestureFor
        Assert.False(s.KeyBindings.ContainsKey("file.save"));
        Assert.Equal("Ctrl+S", s.GestureFor("file.save"));
    }

    [Fact]
    public void Apply_sable_clears_overrides()
    {
        var s = new SableSettings();
        s.KeyBindings["edit.redo"] = "Ctrl+Shift+Z";
        KeymapPresets.Apply(s, KeymapPresets.Sable);
        Assert.Empty(s.KeyBindings);
        Assert.Equal("Ctrl+Y", s.GestureFor("edit.redo"));   // back to Sable default
    }

    [Fact]
    public void All_presets_have_unique_ids_and_resolve()
    {
        Assert.Equal(KeymapPresets.All.Count, KeymapPresets.All.Select(p => p.Id).Distinct().Count());
        foreach (var p in KeymapPresets.All) Assert.Same(p, KeymapPresets.ById(p.Id));
    }
}

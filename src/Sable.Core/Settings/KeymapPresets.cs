namespace Sable.Core.Settings;

/// <summary>A named keyboard-shortcut migration preset (ROADMAP P3 / PLAN §17.1): a set of
/// gesture overrides keyed by command id, relative to the catalog defaults. An empty override
/// map means "the Sable defaults". Gestures use the <c>KeyGesture.Parse</c> grammar.</summary>
public sealed record KeymapPreset(string Id, string Name, IReadOnlyDictionary<string, string> Overrides);

/// <summary>
/// Built-in keymap presets so a user migrating from Photoshop / Affinity Photo can swap the whole
/// shortcut set in one action (ROADMAP P3 "Shortcut migration presets"). Pure data + apply logic —
/// the UI (SettingsWindow ▸ Keyboard) lists these and applies one to the working key map. Presets
/// store only the gestures that DIFFER from the Sable defaults; everything else falls through to
/// the catalog default, so the basics Photoshop/Affinity already share need no entry.
/// A preset is a starting point the user can then tweak per-command.
/// </summary>
public static class KeymapPresets
{
    public static readonly KeymapPreset Sable =
        new("sable", "Sable (Default)", new Dictionary<string, string>());

    public static readonly KeymapPreset Photoshop =
        new("photoshop", "Photoshop", new Dictionary<string, string>
        {
            ["edit.redo"] = "Ctrl+Shift+Z",   // PS redo (Sable uses Ctrl+Y)
            ["layer.new"] = "Ctrl+Shift+N",   // PS New Layer
        });

    public static readonly KeymapPreset Affinity =
        new("affinity", "Affinity Photo", new Dictionary<string, string>
        {
            ["layer.new"] = "Ctrl+Shift+N",   // Affinity Add Pixel Layer (redo stays Ctrl+Y, like Sable)
        });

    public static readonly IReadOnlyList<KeymapPreset> All = new[] { Sable, Photoshop, Affinity };

    public static KeymapPreset? ById(string id)
    {
        foreach (var p in All) if (p.Id == id) return p;
        return null;
    }

    private static string CatalogDefault(string id)
    {
        foreach (var c in KeyCommands.Catalog) if (c.Id == id) return c.DefaultGesture;
        return "";
    }

    /// <summary>The effective gesture for a command under a preset: its override, else the catalog default.</summary>
    public static string GestureFor(KeymapPreset preset, string id)
        => preset.Overrides.TryGetValue(id, out var g) ? g : CatalogDefault(id);

    /// <summary>Write a preset into settings: clear existing overrides, keep only the entries that
    /// actually differ from the catalog default (mirrors how SettingsWindow persists rebinds).</summary>
    public static void Apply(SableSettings s, KeymapPreset preset)
    {
        s.KeyBindings.Clear();
        foreach (var c in KeyCommands.Catalog)
        {
            var g = GestureFor(preset, c.Id);
            if (g != c.DefaultGesture) s.KeyBindings[c.Id] = g;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sable.Core.Settings;

/// <summary>App theme (chrome variant). Dark default; Gray/Light selectable in Preferences (PLAN §17.1).</summary>
public enum AppTheme { Dark, Gray, Light }

/// <summary>
/// Persisted application settings (PLAN §17.1): theme, defaults, recent files, window/session
/// state. Stored as JSON under %AppData%/Sable (platform-equivalent). Pure POCO — no UI deps,
/// so it lives in Sable.Core and is unit-testable.
/// </summary>
public sealed class SableSettings
{
    // --- General ---
    public bool ReopenOnStartup { get; set; } = true;       // restores the last session
    public bool LimitInitialZoom { get; set; }              // never zoom past 100% on open
    public double DefaultDpi { get; set; } = 96;            // New-document default

    // --- User Interface ---
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // --- User Interface: canvas-overlay appearance (PLAN §17.1) ---
    // Hex "#RRGGBB" overlay colours. Defaults match the built-in shader constants.
    public string GuideColor { get; set; } = "#00A0E6";        // guide lines (cyan)
    public string SmartGuideColor { get; set; } = "#FF3399";   // smart-guide alignment lines (magenta)
    public string GridColor { get; set; } = "#808080";         // document/pixel grid (grey)
    public string QuickMaskColor { get; set; } = "#F21A33";    // quick-mask rubylith (red)

    // --- Machine Learning (PLAN §6 / Phase 8) ---
    /// <summary>On-device AI opt-in. Off by default; the AI menu is hidden until enabled, and enabling
    /// prompts to download the model set. No AI runs (and no models download) unless the user turns this on.</summary>
    public bool AiEnabled { get; set; }

    // --- Performance ---
    public int UndoLimit { get; set; } = 256;              // per-document undo capacity

    // --- Autosave / recovery (PLAN §2.6) ---
    public bool AutosaveEnabled { get; set; } = true;
    public int AutosaveMinutes { get; set; } = 5;          // autosave interval for crash recovery

    // --- Updates ---
    public bool AutoCheckUpdates { get; set; } = true;     // consumed by UpdateService (Phase 2 #4)

    /// <summary>Keyboard overrides: command id → gesture string (KeyGesture.Parse grammar). Absent =
    /// the command's <see cref="KeyCommandInfo.DefaultGesture"/>. "" = explicitly unbound.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new();

    /// <summary>Effective gesture for a command id: override if present, else the catalog default.</summary>
    public string GestureFor(string id)
    {
        if (KeyBindings.TryGetValue(id, out var g)) return g ?? "";
        foreach (var c in KeyCommands.Catalog) if (c.Id == id) return c.DefaultGesture;
        return "";
    }

    /// <summary>Most-recently-opened/saved file paths (newest first, capped).</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Saved-file tab paths open at last exit, to restore the session.</summary>
    public List<string> OpenTabs { get; set; } = new();

    // window placement
    public double? WinX { get; set; }
    public double? WinY { get; set; }
    public double WinW { get; set; } = 1280;
    public double WinH { get; set; } = 760;
    public bool WinMaximized { get; set; }

    public const int MaxRecent = 12;

    /// <summary>Parse a "#RRGGBB" (or "RRGGBB") hex colour to bytes; returns the fallback on failure.</summary>
    public static (byte R, byte G, byte B) ParseHex(string? hex, (byte, byte, byte) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);   // tolerate #AARRGGBB
        if (s.Length != 6) return fallback;
        try
        {
            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            return (r, g, b);
        }
        catch { return fallback; }
    }

    public (byte, byte, byte) GuideRgb() => ParseHex(GuideColor, (0, 160, 230));
    public (byte, byte, byte) SmartGuideRgb() => ParseHex(SmartGuideColor, (255, 51, 153));
    public (byte, byte, byte) GridRgb() => ParseHex(GridColor, (128, 128, 128));
    public (byte, byte, byte) QuickMaskRgb() => ParseHex(QuickMaskColor, (242, 26, 51));

    /// <summary>Push a path to the front of the recent list (de-duplicated, capped).</summary>
    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
    }
}

/// <summary>Loads/saves <see cref="SableSettings"/> as JSON (PLAN §17.1).</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%AppData%/Sable/settings.json (or platform equivalent).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "settings.json");

    public static SableSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<SableSettings>(File.ReadAllText(path), Opts) ?? new SableSettings();
        }
        catch { /* corrupt/unreadable → defaults */ }
        return new SableSettings();
    }

    public static void Save(SableSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Opts));
        }
        catch { /* best-effort */ }
    }
}

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

    // --- Performance ---
    public int UndoLimit { get; set; } = 256;              // per-document undo capacity

    // --- Autosave / recovery (PLAN §2.6) ---
    public bool AutosaveEnabled { get; set; } = true;
    public int AutosaveMinutes { get; set; } = 5;          // autosave interval for crash recovery

    // --- Updates ---
    public bool AutoCheckUpdates { get; set; } = true;     // consumed by UpdateService (Phase 2 #4)

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

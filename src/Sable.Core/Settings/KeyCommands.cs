namespace Sable.Core.Settings;

/// <summary>One rebindable command: stable <see cref="Id"/>, display <see cref="Label"/>, a UI
/// <see cref="Category"/> for grouping, and a <see cref="DefaultGesture"/> ("" = unbound).</summary>
public sealed record KeyCommandInfo(string Id, string Label, string Category, string DefaultGesture);

/// <summary>
/// Catalog of rebindable command actions (PLAN §17.1 keymap). Pure data — the gesture STRINGS use
/// Avalonia's <c>KeyGesture.Parse</c> grammar ("Ctrl+Shift+C", "Ctrl+OemPlus", "Ctrl+D0"). The
/// id→handler wiring lives in the UI (MainWindow); this stays UI-agnostic so it can live in Core.
/// Tool-cycle letters and raw navigation keys are deliberately NOT here (fixed Photoshop-standard).
/// </summary>
public static class KeyCommands
{
    public static readonly IReadOnlyList<KeyCommandInfo> Catalog = new[]
    {
        // File
        new KeyCommandInfo("file.new",        "New Document",      "File",   "Ctrl+N"),
        new KeyCommandInfo("file.open",       "Open",              "File",   "Ctrl+O"),
        new KeyCommandInfo("file.openImage",  "Open Image",        "File",   ""),
        new KeyCommandInfo("file.save",       "Save",              "File",   "Ctrl+S"),
        new KeyCommandInfo("file.saveAs",     "Save As",           "File",   "Ctrl+Shift+S"),
        new KeyCommandInfo("file.export",     "Export",            "File",   ""),
        new KeyCommandInfo("file.closeTab",   "Close Tab",         "File",   "Ctrl+W"),
        // Edit
        new KeyCommandInfo("edit.undo",       "Undo",              "Edit",   "Ctrl+Z"),
        new KeyCommandInfo("edit.redo",       "Redo",              "Edit",   "Ctrl+Y"),
        new KeyCommandInfo("edit.cut",        "Cut",               "Edit",   "Ctrl+X"),
        new KeyCommandInfo("edit.copy",       "Copy",              "Edit",   "Ctrl+C"),
        new KeyCommandInfo("edit.copyMerged", "Copy Merged",       "Edit",   "Ctrl+Shift+C"),
        new KeyCommandInfo("edit.paste",      "Paste",             "Edit",   "Ctrl+V"),
        new KeyCommandInfo("edit.pasteInto",  "Paste Into",        "Edit",   "Ctrl+Shift+V"),
        new KeyCommandInfo("edit.duplicate",  "Duplicate Layer",   "Edit",   "Ctrl+J"),
        // Select
        new KeyCommandInfo("select.all",      "Select All",        "Select", "Ctrl+A"),
        new KeyCommandInfo("select.deselect", "Deselect",          "Select", "Ctrl+D"),
        new KeyCommandInfo("select.invert",   "Invert Selection",  "Select", "Ctrl+Shift+I"),
        // Layer
        new KeyCommandInfo("layer.new",          "New Layer",      "Layer",  ""),
        new KeyCommandInfo("layer.mergeDown",    "Merge Down",     "Layer",  "Ctrl+E"),
        new KeyCommandInfo("layer.mergeVisible", "Merge Visible",  "Layer",  "Ctrl+Shift+E"),
        new KeyCommandInfo("layer.stamp",        "Stamp Visible",  "Layer",  "Ctrl+Shift+Alt+E"),
        // View
        new KeyCommandInfo("view.zoomIn",     "Zoom In",           "View",   "Ctrl+OemPlus"),
        new KeyCommandInfo("view.zoomOut",    "Zoom Out",          "View",   "Ctrl+OemMinus"),
        new KeyCommandInfo("view.fit",        "Fit to Window",     "View",   "Ctrl+D0"),
        new KeyCommandInfo("view.actual",     "Actual Pixels",     "View",   "Ctrl+D1"),
        // Window
        new KeyCommandInfo("window.palette",     "Command Palette",      "Window", "Ctrl+K"),
        new KeyCommandInfo("window.history",     "History Panel",        "Window", ""),
        new KeyCommandInfo("window.adjustments", "Adjustments Panel",    "Window", ""),
        new KeyCommandInfo("window.effects",     "Layer Effects Panel",  "Window", ""),
    };
}

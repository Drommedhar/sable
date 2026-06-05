using System;
using System.IO;
using Sable.Core.Settings;

namespace Sable.Core.Ai;

/// <summary>
/// Keeps generative-preset workflows self-contained. A preset's <see cref="GenerativePreset.WorkflowFile"/>
/// must point at a private copy under <see cref="SableSettings.WorkflowsFolder"/>, NOT the user's original
/// export (which they may delete or move). This copies workflows in on save and migrates legacy presets that
/// still reference an external path.
/// </summary>
public static class WorkflowStore
{
    /// <summary>True when <paramref name="path"/> already lives inside our workflows folder.</summary>
    public static bool IsOwned(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var folder = SableSettings.WorkflowsFolder;
        var full = Path.GetFullPath(path);
        var ownedDir = Path.GetFullPath(folder);
        return full.StartsWith(ownedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(ownedDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copy <paramref name="srcPath"/> into the workflows folder under a unique name and return the
    /// copy's full path. Returns null if the source is missing/unreadable.</summary>
    public static string? CopyIn(string srcPath)
    {
        if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath)) return null;
        var folder = SableSettings.WorkflowsFolder;
        Directory.CreateDirectory(folder);
        var name = SanitizeName(Path.GetFileNameWithoutExtension(srcPath));
        var dest = Path.Combine(folder, $"{name}_{Guid.NewGuid():N}.json");
        try { File.Copy(srcPath, dest, overwrite: false); }
        catch { return null; }
        return dest;
    }

    /// <summary>Delete a workflow copy we own (no-op for external paths, so we never delete a user file).</summary>
    public static void DeleteOwned(string? path)
    {
        if (!IsOwned(path)) return;
        try { if (File.Exists(path!)) File.Delete(path!); } catch { /* best-effort */ }
    }

    /// <summary>One-time migration: copy any legacy preset workflow that still points at an external file into
    /// our storage and repoint the preset. Missing originals are left untouched (the preset reports the loss).
    /// Returns true if anything changed (caller should save settings).</summary>
    public static bool Migrate(SableSettings settings)
    {
        bool changed = false;
        foreach (var p in settings.GenerativePresets)
        {
            if (string.IsNullOrWhiteSpace(p.WorkflowFile) || IsOwned(p.WorkflowFile)) continue;
            var copied = CopyIn(p.WorkflowFile!);
            if (copied is null) continue;   // original gone — keep the old path so the UI can flag it
            p.WorkflowFile = copied;
            changed = true;
        }
        return changed;
    }

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "workflow" : name;
    }
}

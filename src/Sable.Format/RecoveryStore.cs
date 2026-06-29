using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sable.Engine;

namespace Sable.Format;

/// <summary>
/// Pure autosave/crash-recovery store (PLAN §2.6). Writes each supplied (dirty) document to a
/// recovery folder as a <c>.sable</c> copy plus a JSON manifest mapping recovery files back to
/// their original path/title. A clean exit calls <see cref="Clear"/>; anything left on next
/// launch means the previous run crashed → <see cref="GetPending"/> surfaces it for restore.
///
/// The folder is a parameter (not %AppData%) so this is headless-testable; the app wraps it with
/// the real recovery directory in <c>Sable.App/RecoveryService</c>.
/// </summary>
public static class RecoveryStore
{
    public static string ManifestPath(string dir) => Path.Combine(dir, "manifest.json");

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string? OrigPath { get; set; }
        public string Title { get; set; } = "Untitled";
    }

    public readonly record struct Pending(string RecoveryPath, string? OrigPath, string Title);

    /// <summary>Write a recovery copy of each supplied document + rebuild the manifest in <paramref name="dir"/>.</summary>
    public static void Save(string dir, IEnumerable<(string id, string? origPath, string title, Document doc)> docs)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var entries = new List<Entry>();
            foreach (var (id, orig, title, doc) in docs)
            {
                SableFile.Save(doc, Path.Combine(dir, id + ".sable"));
                entries.Add(new Entry { Id = id, OrigPath = orig, Title = title });
            }
            File.WriteAllText(ManifestPath(dir), JsonSerializer.Serialize(entries));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Recovery files left in <paramref name="dir"/> from a previous (unclean) run, if any.</summary>
    public static List<Pending> GetPending(string dir)
    {
        var result = new List<Pending>();
        try
        {
            var manifest = ManifestPath(dir);
            if (!File.Exists(manifest)) return result;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(manifest)) ?? new();
            foreach (var e in entries)
            {
                var p = Path.Combine(dir, e.Id + ".sable");
                if (File.Exists(p)) result.Add(new Pending(p, e.OrigPath, e.Title));
            }
        }
        catch { /* corrupt → nothing to recover */ }
        return result;
    }

    /// <summary>Delete the recovery folder (called on a clean exit, and after a restore).</summary>
    public static void Clear(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

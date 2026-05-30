using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sable.Engine;
using Sable.Format;

namespace Sable.App;

/// <summary>
/// Autosave + crash recovery (PLAN §2.6). Periodically writes each dirty open document to a
/// recovery folder (%AppData%/Sable/Recovery) plus a manifest. A clean exit clears the folder,
/// so anything left on next launch means the previous run crashed → offer to restore it.
/// </summary>
public static class RecoveryService
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "Recovery");
    private static string ManifestPath => Path.Combine(Dir, "manifest.json");

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string? OrigPath { get; set; }
        public string Title { get; set; } = "Untitled";
    }

    public readonly record struct Pending(string RecoveryPath, string? OrigPath, string Title);

    /// <summary>Write a recovery copy of each supplied (dirty) document + rebuild the manifest.</summary>
    public static void Save(IEnumerable<(string id, string? origPath, string title, Document doc)> docs)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var entries = new List<Entry>();
            foreach (var (id, orig, title, doc) in docs)
            {
                var p = Path.Combine(Dir, id + ".sable");
                SableFile.Save(doc, p);
                entries.Add(new Entry { Id = id, OrigPath = orig, Title = title });
            }
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(entries));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Recovery files left from a previous (unclean) run, if any.</summary>
    public static List<Pending> GetPending()
    {
        var result = new List<Pending>();
        try
        {
            if (!File.Exists(ManifestPath)) return result;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(ManifestPath)) ?? new();
            foreach (var e in entries)
            {
                var p = Path.Combine(Dir, e.Id + ".sable");
                if (File.Exists(p)) result.Add(new Pending(p, e.OrigPath, e.Title));
            }
        }
        catch { /* corrupt → nothing to recover */ }
        return result;
    }

    /// <summary>Delete the recovery folder (called on a clean exit, and after a restore).</summary>
    public static void Clear()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

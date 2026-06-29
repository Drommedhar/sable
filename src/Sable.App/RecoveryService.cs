using System;
using System.Collections.Generic;
using System.IO;
using Sable.Engine;
using Sable.Format;

namespace Sable.App;

/// <summary>
/// App wrapper around <see cref="RecoveryStore"/> (PLAN §2.6): binds the pure store to the real
/// recovery folder (%AppData%/Sable/Recovery). Periodically writes each dirty open document there;
/// a clean exit clears the folder, so anything left on next launch means the previous run crashed
/// → offer to restore it. All logic/round-trip lives in <see cref="RecoveryStore"/> (testable).
/// </summary>
public static class RecoveryService
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "Recovery");

    public readonly record struct Pending(string RecoveryPath, string? OrigPath, string Title);

    public static void Save(IEnumerable<(string id, string? origPath, string title, Document doc)> docs)
        => RecoveryStore.Save(Dir, docs);

    public static List<Pending> GetPending()
    {
        var result = new List<Pending>();
        foreach (var p in RecoveryStore.GetPending(Dir))
            result.Add(new Pending(p.RecoveryPath, p.OrigPath, p.Title));
        return result;
    }

    public static void Clear() => RecoveryStore.Clear(Dir);
}

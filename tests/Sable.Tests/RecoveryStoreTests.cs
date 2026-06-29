using System.IO;
using System.Linq;
using Sable.Engine;
using Sable.Format;

namespace Sable.Tests;

/// <summary>Autosave/crash-recovery round-trip (PLAN §2.6 / ROADMAP P3).</summary>
public sealed class RecoveryStoreTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "sable_rec_" + System.Guid.NewGuid().ToString("N"));
        return d;
    }

    [Fact]
    public void Save_then_GetPending_lists_each_doc_with_metadata_and_loadable_copy()
    {
        var dir = TempDir();
        try
        {
            var a = Document.CreateDemo(64, 48);
            var b = Document.CreateDemo(32, 32);
            RecoveryStore.Save(dir, new (string, string?, string, Document)[]
            {
                ("idA", @"C:\work\a.sable", "A", a),
                ("idB", null,               "B", b),
            });

            var pending = RecoveryStore.GetPending(dir);
            Assert.Equal(2, pending.Count);

            var pa = pending.Single(p => p.Title == "A");
            Assert.Equal(@"C:\work\a.sable", pa.OrigPath);
            Assert.True(File.Exists(pa.RecoveryPath));

            var pb = pending.Single(p => p.Title == "B");
            Assert.Null(pb.OrigPath);   // never-saved doc → no original path

            // the recovery copy must be a real loadable .sable preserving dimensions
            var loaded = SableFile.Load(pa.RecoveryPath);
            Assert.Equal(64, loaded.Width);
            Assert.Equal(48, loaded.Height);
        }
        finally { RecoveryStore.Clear(dir); }
    }

    [Fact]
    public void Clear_removes_pending()
    {
        var dir = TempDir();
        RecoveryStore.Save(dir, new (string, string?, string, Document)[]
        {
            ("id1", null, "X", Document.CreateDemo(16, 16)),
        });
        Assert.Single(RecoveryStore.GetPending(dir));

        RecoveryStore.Clear(dir);
        Assert.Empty(RecoveryStore.GetPending(dir));   // clean exit → no false recovery prompt
    }

    [Fact]
    public void GetPending_on_missing_dir_is_empty()
        => Assert.Empty(RecoveryStore.GetPending(TempDir()));
}

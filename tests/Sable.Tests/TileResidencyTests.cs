using Sable.Engine.Compositing;
using Xunit;

namespace Sable.Tests;

public class TileResidencyTests
{
    private static TileResidency.Key K(object o, int x, int y) => new(o, x, y);

    [Fact]
    public void Acquire_NewTile_NeedsUpload_AndBecomesResident()
    {
        var owner = new object();
        var r = new TileResidency(4);
        int slot = r.Acquire(K(owner, 0, 0), out bool up);
        Assert.True(up);
        Assert.Equal(1, r.ResidentCount);
        Assert.True(r.TryGet(K(owner, 0, 0), out int got));
        Assert.Equal(slot, got);
    }

    [Fact]
    public void Acquire_Resident_DoesNotNeedUpload()
    {
        var owner = new object();
        var r = new TileResidency(4);
        int s1 = r.Acquire(K(owner, 1, 1), out _);
        int s2 = r.Acquire(K(owner, 1, 1), out bool up);
        Assert.Equal(s1, s2);
        Assert.False(up);
    }

    [Fact]
    public void Acquire_OverCapacity_EvictsLeastRecentlyUsed()
    {
        var owner = new object();
        var r = new TileResidency(2);
        r.Acquire(K(owner, 0, 0), out _);   // LRU after next
        r.Acquire(K(owner, 1, 0), out _);
        r.Acquire(K(owner, 2, 0), out _);   // evicts (0,0)
        Assert.Equal(2, r.ResidentCount);
        Assert.Equal(1, r.EvictionCount);
        Assert.False(r.TryGet(K(owner, 0, 0), out _));   // evicted
        Assert.True(r.TryGet(K(owner, 1, 0), out _));
        Assert.True(r.TryGet(K(owner, 2, 0), out _));
    }

    [Fact]
    public void Touch_ViaReacquire_ChangesEvictionOrder()
    {
        var owner = new object();
        var r = new TileResidency(2);
        r.Acquire(K(owner, 0, 0), out _);
        r.Acquire(K(owner, 1, 0), out _);
        r.Acquire(K(owner, 0, 0), out _);   // touch (0,0) → now MRU, (1,0) is LRU
        r.Acquire(K(owner, 2, 0), out _);   // evicts (1,0), not (0,0)
        Assert.True(r.TryGet(K(owner, 0, 0), out _));
        Assert.False(r.TryGet(K(owner, 1, 0), out _));
    }

    [Fact]
    public void Invalidate_FreesSlot_AndForcesReupload()
    {
        var owner = new object();
        var r = new TileResidency(4);
        r.Acquire(K(owner, 3, 3), out _);
        r.Invalidate(K(owner, 3, 3));
        Assert.Equal(0, r.ResidentCount);
        r.Acquire(K(owner, 3, 3), out bool up);
        Assert.True(up);   // re-upload required
    }

    [Fact]
    public void ReleaseOwner_FreesAllOfThatLayersTiles_Only()
    {
        var a = new object();
        var b = new object();
        var r = new TileResidency(8);
        r.Acquire(K(a, 0, 0), out _);
        r.Acquire(K(a, 1, 0), out _);
        r.Acquire(K(b, 0, 0), out _);
        r.ReleaseOwner(a);
        Assert.False(r.TryGet(K(a, 0, 0), out _));
        Assert.False(r.TryGet(K(a, 1, 0), out _));
        Assert.True(r.TryGet(K(b, 0, 0), out _));
        Assert.Equal(1, r.ResidentCount);
    }

    [Fact]
    public void RefillAfterFullEviction_ReusesSlots_NoLeak()
    {
        var owner = new object();
        var r = new TileResidency(2);
        for (int i = 0; i < 20; i++) r.Acquire(K(owner, i, 0), out _);
        Assert.Equal(2, r.ResidentCount);    // never exceeds capacity
        Assert.Equal(18, r.EvictionCount);
    }
}

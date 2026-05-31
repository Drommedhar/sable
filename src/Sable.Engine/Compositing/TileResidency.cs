namespace Sable.Engine.Compositing;

/// <summary>
/// Pure residency policy for a GPU tile atlas (PLAN §3 / §17.3): maps tile keys
/// (owner layer + tile coord) to a bounded set of atlas slots with LRU eviction.
/// No GPU dependency — <see cref="GpuCompositor"/> drives the uploads; this just
/// decides which slot a tile lives in and which cold tile to evict when full.
///
/// Invariant: a single composite pass of one layer must request no more DISTINCT
/// tiles than <see cref="MaxSlots"/>, otherwise a slot referenced earlier in the
/// pass could be evicted mid-build. The compositor falls back to a monolithic
/// buffer for any layer whose live tile count exceeds the atlas.
/// </summary>
public sealed class TileResidency
{
    /// <summary>Identifies one tile: its owning layer (reference identity) + tile grid coord.</summary>
    public readonly struct Key : System.IEquatable<Key>
    {
        public readonly object Owner;
        public readonly int Tx;
        public readonly int Ty;
        public Key(object owner, int tx, int ty) { Owner = owner; Tx = tx; Ty = ty; }
        public bool Equals(Key o) => ReferenceEquals(Owner, o.Owner) && Tx == o.Tx && Ty == o.Ty;
        public override bool Equals(object? o) => o is Key k && Equals(k);
        public override int GetHashCode() =>
            System.HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Owner), Tx, Ty);
    }

    private readonly int _maxSlots;
    private readonly Dictionary<Key, int> _map = new();          // key → slot
    private readonly Key[] _slotKey;                             // slot → key (valid where _slotUsed)
    private readonly bool[] _slotUsed;
    private readonly Stack<int> _free = new();                   // unused slots
    private readonly LinkedList<int> _lru = new();               // front = most recently used slot
    private readonly LinkedListNode<int>[] _node;               // slot → its LRU node

    public TileResidency(int maxSlots)
    {
        _maxSlots = System.Math.Max(1, maxSlots);
        _slotKey = new Key[_maxSlots];
        _slotUsed = new bool[_maxSlots];
        _node = new LinkedListNode<int>[_maxSlots];
        for (int i = _maxSlots - 1; i >= 0; i--) _free.Push(i);
    }

    public int MaxSlots => _maxSlots;
    public int ResidentCount => _map.Count;
    public long EvictionCount { get; private set; }

    public bool TryGet(Key key, out int slot) => _map.TryGetValue(key, out slot);

    /// <summary>
    /// Ensure <paramref name="key"/> has a slot, returning it. <paramref name="needUpload"/> is true
    /// when the slot must be (re)filled by the caller (new residency or reclaimed from eviction).
    /// Touches LRU so the tile is now most-recently-used.
    /// </summary>
    public int Acquire(Key key, out bool needUpload)
    {
        if (_map.TryGetValue(key, out var slot))
        {
            Touch(slot);
            needUpload = false;
            return slot;
        }
        slot = _free.Count > 0 ? _free.Pop() : Evict();
        _map[key] = slot;
        _slotKey[slot] = key;
        _slotUsed[slot] = true;
        _node[slot] = _lru.AddFirst(slot);
        needUpload = true;
        return slot;
    }

    private void Touch(int slot)
    {
        var n = _node[slot];
        if (n is not null && !ReferenceEquals(n, _lru.First)) { _lru.Remove(n); _lru.AddFirst(n); }
    }

    private int Evict()
    {
        var node = _lru.Last!;            // least recently used
        int slot = node.Value;
        _lru.RemoveLast();
        _map.Remove(_slotKey[slot]);
        _slotUsed[slot] = false;
        _node[slot] = null!;
        EvictionCount++;
        return slot;
    }

    /// <summary>Drop a tile's residency (e.g. it was painted → must re-upload on next Acquire).</summary>
    public void Invalidate(Key key)
    {
        if (!_map.TryGetValue(key, out var slot)) return;
        _map.Remove(key);
        if (_node[slot] is { } n) _lru.Remove(n);
        _slotUsed[slot] = false;
        _node[slot] = null!;
        _free.Push(slot);
    }

    /// <summary>Free every slot belonging to a layer (layer removed / document swapped).</summary>
    public void ReleaseOwner(object owner)
    {
        var drop = new List<Key>();
        foreach (var k in _map.Keys) if (ReferenceEquals(k.Owner, owner)) drop.Add(k);
        foreach (var k in drop) Invalidate(k);
    }

    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
        _free.Clear();
        for (int i = _maxSlots - 1; i >= 0; i--) { _slotUsed[i] = false; _node[i] = null!; _free.Push(i); }
    }
}

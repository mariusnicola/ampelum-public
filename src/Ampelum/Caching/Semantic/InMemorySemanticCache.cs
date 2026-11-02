using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Caching.Semantic;

/// <summary>
/// Thread-safe sliding-window (LRU) implementation of <see cref="ISemanticCache"/> backed by
/// a doubly-linked list + dictionary. The capacity is fixed at construction; on overflow the
/// least-recently-touched entry is evicted (PSQ-2, v0.9g).
/// </summary>
/// <remarks>
/// Coarse-grained locking is sufficient: the hook pipeline is the only writer, contention is
/// dominated by upstream LM latency, and the critical section is O(1).
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class InMemorySemanticCache : ISemanticCache
{
    sealed class Node
    {
        public required string Key { get; init; }
        public required SemanticCacheEntry Value { get; set; }
        public Node? Prev;
        public Node? Next;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Node> _map;
    readonly int _capacity;
    Node? _head; // most-recently-used
    Node? _tail; // least-recently-used

    /// <summary>Creates a cache with capacity <paramref name="capacity"/>.</summary>
    /// <param name="capacity">Maximum entry count. Must be at least 1; defaults to 256.</param>
    public InMemorySemanticCache(int capacity = 256)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be >= 1.");
        _capacity = capacity;
        _map = new Dictionary<string, Node>(capacity, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public int Count
    {
        get
        {
            lock (_gate) return _map.Count;
        }
    }

    /// <inheritdoc/>
    public SemanticCacheEntry? TryGet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
                return null;
            MoveToHead(node);
            return node.Value;
        }
    }

    /// <inheritdoc/>
    public void Set(string key, SemanticCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = entry;
                MoveToHead(existing);
                return;
            }
            var node = new Node { Key = key, Value = entry };
            _map[key] = node;
            InsertAtHead(node);
            if (_map.Count > _capacity)
                EvictTail();
        }
    }

    void InsertAtHead(Node node)
    {
        node.Prev = null;
        node.Next = _head;
        if (_head is not null) _head.Prev = node;
        _head = node;
        _tail ??= node;
    }

    void Unlink(Node node)
    {
        // Callers (MoveToHead, EvictTail) never invoke this with the current head, so
        // node.Prev is always non-null. The Next-side guard remains because the tail's
        // Next is null.
        node.Prev!.Next = node.Next;
        if (node.Next is not null) node.Next.Prev = node.Prev;
        else _tail = node.Prev;
        node.Prev = null;
        node.Next = null;
    }

    void MoveToHead(Node node)
    {
        if (ReferenceEquals(node, _head)) return;
        Unlink(node);
        InsertAtHead(node);
    }

    void EvictTail()
    {
        var victim = _tail;
        if (victim is null) return;
        Unlink(victim);
        _map.Remove(victim.Key);
    }
}

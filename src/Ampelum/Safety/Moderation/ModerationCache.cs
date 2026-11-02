using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using System.Threading;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Moderation;

/// <summary>Bounded LRU cache for moderation verdicts. Keys: XxHash64 of input text. Thread-safe.</summary>
internal sealed class ModerationCache
{
    readonly int _capacity;
    readonly LinkedList<(ulong Key, ModerationResult Value)> _list = new();
    readonly Dictionary<ulong, LinkedListNode<(ulong Key, ModerationResult Value)>> _map = new();
    readonly Lock _gate = new();

    public ModerationCache(int capacity) => _capacity = capacity;

    public bool TryGet(string text, out ModerationResult result)
    {
        var key = Hash(text);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                result = node.Value.Value;
                return true;
            }
        }
        result = default!;
        return false;
    }

    public void Set(string text, ModerationResult result)
    {
        var key = Hash(text);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(key);
            }
            var node = _list.AddFirst((key, result));
            _map[key] = node;
            if (_map.Count > _capacity)
            {
                var last = _list.Last!;
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    static ulong Hash(string text) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));
}

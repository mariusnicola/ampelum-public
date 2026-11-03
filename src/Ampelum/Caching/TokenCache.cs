#pragma warning disable AMPELUM_V09_PREVIEW

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Caching;

/// <summary>Exact-match cache for LLM responses with configurable TTL.</summary>
public sealed class TokenCache : IDisposable
{
    readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    readonly TimeSpan _ttl;
    readonly Timer _cleanupTimer;
    readonly int _maxEntries;
    bool _disposed;

    /// <param name="ttl">Time-to-live for cached entries. Default 5 minutes.</param>
    /// <param name="cleanupInterval">How often to purge expired entries. Default 1 minute.</param>
    /// <param name="maxEntries">Maximum cache entries before eager eviction. Default 1000.</param>
    public TokenCache(TimeSpan? ttl = null, TimeSpan? cleanupInterval = null, int maxEntries = 1000)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _maxEntries = maxEntries;
        _cleanupTimer = new Timer(_ => PurgeExpired(), null,
            cleanupInterval ?? TimeSpan.FromMinutes(1),
            cleanupInterval ?? TimeSpan.FromMinutes(1));
    }

    const string KeyVersion = "TokenCache.v2";

    /// <summary>
    /// True when a request with these options can be keyed and served from the cache. A raw-representation
    /// factory, a continuation token and background responses carry state the key cannot represent.
    /// </summary>
    public static bool IsCacheable(ChatOptions? options)
    {
#pragma warning disable MEAI001 // experimental MEAI members, read only to refuse caching
        return options is null
            || (options.RawRepresentationFactory is null
                && options.ContinuationToken is null
                && options.AllowBackgroundResponses is not true);
#pragma warning restore MEAI001
    }

    /// <summary>
    /// Compute a cache key from the whole request: every message (role, author, all content, additional
    /// properties) and every serializable option, with each tool taken as its name, description and JSON
    /// schema. Null options and empty options give the same key. Throws <see cref="NotSupportedException"/>
    /// or <see cref="System.Text.Json.JsonException"/> when part of the request cannot be serialized.
    /// </summary>
    public static string BuildKey(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var keyed = options?.Clone() ?? new ChatOptions();
        var tools = (keyed.Tools ?? []).Select(ToolIdentity).ToList();
        keyed.Tools = null;
        return AIJsonUtilities.HashDataToString([KeyVersion, messages.ToList(), keyed, tools], AIJsonUtilities.DefaultOptions);
    }

    static string ToolIdentity(AITool tool) =>
        tool is AIFunctionDeclaration declaration
            ? string.Join('\u001f', declaration.Name, declaration.Description,
                declaration.JsonSchema.GetRawText(), declaration.ReturnJsonSchema?.GetRawText() ?? "")
            : string.Join('\u001f', tool.GetType().FullName, tool.Name, tool.Description);

    /// <summary>Try to get a cached response for the given key.</summary>
    /// <returns>The cached response, or null if not found or expired.</returns>
    public ChatResponse? TryGet(string key)
    {
        if (_disposed) return null;
        if (!_cache.TryGetValue(key, out var entry)) return null;
        if (DateTimeOffset.UtcNow - entry.Created > _ttl)
        {
            _cache.TryRemove(key, out _);
            return null;
        }
        entry.LastAccess = DateTimeOffset.UtcNow;
        return entry.Response;
    }

    /// <summary>Store a response in the cache.</summary>
    public void Set(string key, ChatResponse response)
    {
        if (_disposed) return;
        // Eager eviction when over capacity
        if (_cache.Count >= _maxEntries)
            EvictOne();

        _cache[key] = new CacheEntry(response, DateTimeOffset.UtcNow);
    }

    /// <summary>Number of entries currently in the cache.</summary>
    public int Count => _cache.Count;

    /// <summary>Clear all entries.</summary>
    public void Clear()
    {
        _cache.Clear();
    }

    void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _ttl;
        foreach (var (key, entry) in _cache)
        {
            if (entry.Created < cutoff)
                _cache.TryRemove(key, out _);
        }
    }

    void EvictOne()
    {
        // Evict the oldest entry by last access time
        KeyValuePair<string, CacheEntry>? oldest = null;
        foreach (var kv in _cache)
        {
            if (oldest is null || kv.Value.LastAccess < oldest.Value.Value.LastAccess)
                oldest = kv;
        }
        if (oldest is not null)
            _cache.TryRemove(oldest.Value.Key, out _);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
        _cache.Clear();
    }

    sealed record CacheEntry(ChatResponse Response, DateTimeOffset Created)
    {
        public DateTimeOffset LastAccess { get; set; } = Created;
    }
}

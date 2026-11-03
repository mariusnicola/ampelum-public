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

    /// <summary>Compute a cache key from the chat messages and the options that change the output
    /// (model id, sampling settings, stop sequences, response format, instructions, conversation id,
    /// tool mode and tool names). Options a caller leaves unset add nothing to the key.</summary>
    public static string BuildKey(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var sb = new System.Text.StringBuilder();
        if (options is not null)
        {
            if (options.ModelId is not null)
                sb.Append(options.ModelId).Append('|');
            AppendOption(sb, "temperature", options.Temperature);
            AppendOption(sb, "top_p", options.TopP);
            AppendOption(sb, "top_k", options.TopK);
            AppendOption(sb, "max_tokens", options.MaxOutputTokens);
            AppendOption(sb, "frequency_penalty", options.FrequencyPenalty);
            AppendOption(sb, "presence_penalty", options.PresencePenalty);
            AppendOption(sb, "seed", options.Seed);
            AppendOption(sb, "instructions", options.Instructions);
            AppendOption(sb, "conversation", options.ConversationId);
            AppendOption(sb, "allow_multiple_tool_calls", options.AllowMultipleToolCalls);
            if (options.StopSequences is { Count: > 0 } stops)
                AppendOption(sb, "stop", string.Join('\u001f', stops));
            if (options.ResponseFormat is not null)
                AppendOption(sb, "response_format", options.ResponseFormat is ChatResponseFormatJson json
                    ? "json:" + json.SchemaName + ":" + json.Schema
                    : options.ResponseFormat.GetType().Name);
            if (options.ToolMode is not null)
                AppendOption(sb, "tool_mode", options.ToolMode is RequiredChatToolMode req
                    ? "required:" + req.RequiredFunctionName
                    : options.ToolMode.GetType().Name);
            if (options.Tools is { Count: > 0 } tools)
                AppendOption(sb, "tools", string.Join('\u001f', tools.Select(t => t.Name + "=" + t.Description)));
        }
        foreach (var m in messages)
            sb.Append(m.Role).Append(':').Append(m.Text ?? "").Append('\n');
        return sb.ToString();
    }

    static void AppendOption(System.Text.StringBuilder sb, string name, object? value)
    {
        if (value is null) return;
        sb.Append(name).Append('=').Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)).Append('|');
    }

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

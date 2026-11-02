using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Usage;

/// <summary>Thread-safe default <see cref="IUsageTracker"/>. Uses <see cref="Interlocked"/> for
/// global counters and a concurrent dictionary for per-scope totals.</summary>
public sealed class DefaultUsageTracker : IUsageTracker
{
    long _promptTokens;
    long _completionTokens;
    long _requestCount;
    long _cacheHits;
    readonly ConcurrentDictionary<string, ScopeState> _scopes = new();
    readonly AsyncLocal<Stack<string>?> _activeScopes = new();

    /// <inheritdoc/>
    public void Record(UsageDetails? usage, bool cacheHit)
    {
        Interlocked.Increment(ref _requestCount);
        if (cacheHit) Interlocked.Increment(ref _cacheHits);
        if (usage?.InputTokenCount is long ti) Interlocked.Add(ref _promptTokens, ti);
        if (usage?.OutputTokenCount is long to) Interlocked.Add(ref _completionTokens, to);

        var stack = _activeScopes.Value;
        if (stack is null) return;
        foreach (var name in stack)
        {
            var st = _scopes.GetOrAdd(name, _ => new ScopeState());
            st.Record(usage, cacheHit);
        }
    }

    /// <inheritdoc/>
    public UsageSummary Snapshot()
    {
        var prompt = Interlocked.Read(ref _promptTokens);
        var completion = Interlocked.Read(ref _completionTokens);
        var req = (int)Interlocked.Read(ref _requestCount);
        var hits = (int)Interlocked.Read(ref _cacheHits);
        IReadOnlyDictionary<string, UsageSummary>? per = null;
        if (!_scopes.IsEmpty)
        {
            var d = new Dictionary<string, UsageSummary>(_scopes.Count);
            foreach (var kv in _scopes) d[kv.Key] = kv.Value.Snapshot();
            per = d;
        }
        return new UsageSummary(prompt, completion, prompt + completion, req, hits, per);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Interlocked.Exchange(ref _promptTokens, 0);
        Interlocked.Exchange(ref _completionTokens, 0);
        Interlocked.Exchange(ref _requestCount, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        _scopes.Clear();
    }

    /// <inheritdoc/>
    public IDisposable Scope(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var stack = _activeScopes.Value ??= new Stack<string>();
        stack.Push(name);
        return new Popper(stack);
    }

    sealed class Popper(Stack<string> stack) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (stack.Count > 0) stack.Pop();
        }
    }

    sealed class ScopeState
    {
        long _p, _c, _req, _hits;
        public void Record(UsageDetails? u, bool hit)
        {
            Interlocked.Increment(ref _req);
            if (hit) Interlocked.Increment(ref _hits);
            if (u?.InputTokenCount is long ti) Interlocked.Add(ref _p, ti);
            if (u?.OutputTokenCount is long to) Interlocked.Add(ref _c, to);
        }
        public UsageSummary Snapshot()
        {
            var p = Interlocked.Read(ref _p);
            var c = Interlocked.Read(ref _c);
            return new UsageSummary(p, c, p + c, (int)Interlocked.Read(ref _req),
                (int)Interlocked.Read(ref _hits), PerScope: null);
        }
    }
}

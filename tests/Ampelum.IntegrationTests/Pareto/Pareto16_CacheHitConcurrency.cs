#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using Ampelum.IntegrationTests.Skips;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #16 (master plan Â§6.H). Cache concurrency: a warmed
/// <see cref="SemanticCacheHook"/> backed by <see cref="InMemorySemanticCache"/> serves
/// identical-input parallel forwards from cache. After one warm-up call against the
/// real LM, Nâˆ’1 parallel forwards with the same input must resolve from cache (no LM
/// hit) and the audit chain over the full N entries must verify end-to-end.
/// </summary>
/// <remarks>
/// Scope-reduced from the original "100 parallel" wording to N=8 (1 warm-up + 7 parallel
/// hits) so the real-LM cost stays under the per-test cap. Concurrency assertions stay
/// the same: hits short-circuit before the chat client is invoked, the cache size stays
/// bounded at 1, and the chain hash is consistent.
/// </remarks>
public sealed class Pareto16_CacheHitConcurrency : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto16_CacheHitConcurrency(ParetoTestFixture fx) => _fx = fx;

    /// <summary>One warm-up + Nâˆ’1 cache hits in parallel; cache size bounded; audit chain valid.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task CacheConcurrency_WarmupThenParallelHits_BoundedCache_AuditChainValid()
    {
        Assert.SkipWhen(SkipUnlessRealLm.ShouldSkip, SkipUnlessRealLm.Reason);
        var ct = TestContext.Current.CancellationToken;

        const int Parallel = 7;

        using var chat = new CountingChatClient(OpenRouterClientFactory.Create());
        var cache = new InMemorySemanticCache(capacity: 8);
        var sink = new InMemoryAuditSink();
        var ctx = new AmpelumContext(
            chat,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new IHook[] { new SemanticCacheHook(cache), new AuditHook(sink) } };

        var predict = new Predict<QaSig>(ctx);
        var question = new QaSig { Question = "What is the capital of Portugal?" };

        // Warm-up: 1 real LM call.
        var first = await predict.ForwardAsync(question, ct: ct);
        first.Value.Answer.ShouldNotBeNullOrWhiteSpace();
        chat.CallCount.ShouldBe(1);
        cache.Count.ShouldBe(1);

        // Parallel cache hits: identical inputs short-circuit in OnBefore.
        var tasks = Enumerable.Range(0, Parallel)
            .Select(_ => predict.ForwardAsync(question, ct: ct))
            .ToArray();
        var hits = await Task.WhenAll(tasks);

        // Inner LM was never re-invoked.
        chat.CallCount.ShouldBe(1);
        // Cache stayed bounded at 1 entry â€” identical key replaces, never grows.
        cache.Count.ShouldBe(1);
        // Every hit produced the same answer as the warm-up call.
        foreach (var h in hits)
            h.Value.Answer.ShouldBe(first.Value.Answer);

        // Audit chain has 1 (miss) + Nâˆ’1 (hits, recorded by AuditHook even on hit) entries.
        sink.NextSequence.ShouldBe((long)(Parallel + 1));
        var verification = await AuditChainVerifier.VerifyAsync(sink, ct);
        verification.IsValid.ShouldBeTrue(verification.Reason);
    }

    /// <summary>Lightweight chat-client decorator that counts inner-LM round-trips.</summary>
    sealed class CountingChatClient : Microsoft.Extensions.AI.IChatClient
    {
        readonly Microsoft.Extensions.AI.IChatClient _inner;
        int _count;

        public CountingChatClient(Microsoft.Extensions.AI.IChatClient inner) => _inner = inner;

        public int CallCount => Volatile.Read(ref _count);

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return _inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }
}

#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using Ampelum.IntegrationTests.Skips;
using SignalGrid.Ampelum.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #14 (master plan Â§6.H). Provider chaos: a
/// <see cref="RoutedChatClient"/> wired with two failing fakes as the primary + first
/// secondary and a real OpenRouter client as the final fallback. The router's
/// <see cref="Failover"/> strategy walks the chain on transport exceptions and the call
/// completes via the real-LM endpoint. Audit chain verifies on the single recorded entry.
/// </summary>
/// <remarks>
/// Scope-reduced from the original "kill primary mid-call" wording: an in-flight
/// HttpMessageHandler kill harness is deferred (master plan Â§6.H R-pending). The
/// observable behaviour we can assert today â€” synchronous transport failure on the
/// primary triggers ordered fallback to the next healthy endpoint â€” exercises the same
/// routing code path. A single real-LM hop keeps cost well under the per-test cap.
/// Note: alpha.5 (Gap 2) added the <see cref="AuditEntry.EventType"/> discriminator.
/// The single recorded entry from the successful Predict forward carries the default
/// <c>forward.completed</c> EventType; the typed <c>router.failover</c> row variant
/// (emitted by <see cref="RoutedChatClient"/>'s own failover audit hook) is wired in
/// a separate ChatClient-side hook, not surfaced through this Predict-bound AuditHook.
/// </remarks>
public sealed class Pareto14_LmProviderMisbehaves : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto14_LmProviderMisbehaves(ParetoTestFixture fx) => _fx = fx;

    /// <summary>Two failing endpoints + one healthy real-LM endpoint via Failover; call succeeds.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task ProviderChaos_TwoFailingEndpoints_FailoverReachesRealLm_AuditChainValid()
    {
        Assert.SkipWhen(SkipUnlessRealLm.ShouldSkip, SkipUnlessRealLm.Reason);
        var ct = TestContext.Current.CancellationToken;

        var primary = new ProviderEndpoint("primary-broken",
            new AlwaysFailsChatClient(new HttpRequestException("primary unreachable")));
        var secondary = new ProviderEndpoint("secondary-broken",
            new AlwaysFailsChatClient(new HttpRequestException("secondary unreachable")));
        var tertiary = new ProviderEndpoint("tertiary-real", OpenRouterClientFactory.Create());

        using var router = new RoutedChatClient(
            new[] { primary, secondary, tertiary },
            new Failover());

        var sink = new InMemoryAuditSink();
        var ctx = new AmpelumContext(
            router,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new IHook[] { new AuditHook(sink) } };

        var predict = new Predict<QaSig>(ctx);
        var pred = await predict.ForwardAsync(
            new QaSig { Question = "What is the capital of Spain? Reply with just the city." },
            ct: ct);

        pred.Value.Answer.ShouldNotBeNullOrWhiteSpace();
        sink.NextSequence.ShouldBe(1L);
        var verification = await AuditChainVerifier.VerifyAsync(sink, ct);
        verification.IsValid.ShouldBeTrue(verification.Reason);

        // alpha.5 (Gap 2): the entry carries the default EventType discriminator.
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(ct)) entries.Add(e);
        entries.ShouldHaveSingleItem().EventType.ShouldBe("forward.completed");
    }

    /// <summary>Chat-client whose every method throws the supplied exception.</summary>
    sealed class AlwaysFailsChatClient : Microsoft.Extensions.AI.IChatClient
    {
        readonly Exception _failure;
        public AlwaysFailsChatClient(Exception failure) => _failure = failure;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<Microsoft.Extensions.AI.ChatResponse>(_failure);

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw _failure;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

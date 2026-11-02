using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using Ampelum.IntegrationTests.Skips;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #2 (master plan Â§6.H, plan task Â§Pareto02). ChainOfThought
/// real-LM smoke: a single CoT forward populates the augmented <c>reasoning</c> field
/// (non-empty), the typed answer is non-empty, and the AuditHook records exactly one
/// chain-valid entry. CoT internally wraps Predict's adapter, so success here also
/// proves the augmenting-adapter wiring round-trips through ChatAdapter.
/// </summary>
public sealed class Pareto02_ChainOfThought : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto02_ChainOfThought(ParetoTestFixture fx) => _fx = fx;

    /// <summary>One CoT forward: rationale + answer populated, single audit entry, chain valid.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task ChainOfThought_OneForward_RationaleAndAnswerPopulated_AuditChainValid()
    {
        Assert.SkipWhen(SkipUnlessRealLm.ShouldSkip, SkipUnlessRealLm.Reason);
        var ct = TestContext.Current.CancellationToken;

        using var chat = OpenRouterClientFactory.Create();
        var sink = new InMemoryAuditSink();
        var ctx = new AmpelumContext(
            chat,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new IHook[] { new AuditHook(sink) } };

        var cot = new ChainOfThought<QaSig>(ctx);
        var result = await cot.ForwardAsync(new QaSig { Question = "What is the capital of Italy?" }, ct: ct);

        result.Value.Answer.ShouldNotBeNullOrWhiteSpace();
        result.Reasoning().ShouldNotBeNullOrWhiteSpace();

        sink.NextSequence.ShouldBe(1L);
        var verification = await AuditChainVerifier.VerifyAsync(sink, ct);
        verification.IsValid.ShouldBeTrue(verification.Reason);
    }
}

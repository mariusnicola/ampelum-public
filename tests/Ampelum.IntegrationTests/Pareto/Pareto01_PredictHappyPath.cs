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
/// Pareto integration test #1 (master plan Â§6.H, plan task Â§Pareto01). Predict happy path:
/// a single real-LM <see cref="Predict{TSig}.ForwardAsync"/> through ChatAdapter with an
/// AuditHook attached. Asserts the call returns a non-empty answer, that the hook pipeline
/// appended exactly one audit entry, and that the chain hash verifies.
/// </summary>
public sealed class Pareto01_PredictHappyPath : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto01_PredictHappyPath(ParetoTestFixture fx) => _fx = fx;

    /// <summary>One forward call, one audit entry, chain valid.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task PredictHappyPath_OneForward_OneAuditEntry_ChainValid()
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

        var predict = new Predict<QaSig>(ctx);
        var result = await predict.ForwardAsync(new QaSig { Question = "What is the capital of France?" }, ct: ct);

        result.Value.Answer.ShouldNotBeNullOrWhiteSpace();
        sink.NextSequence.ShouldBe(1L);

        var verification = await AuditChainVerifier.VerifyAsync(sink, ct);
        verification.IsValid.ShouldBeTrue(verification.Reason);
    }
}

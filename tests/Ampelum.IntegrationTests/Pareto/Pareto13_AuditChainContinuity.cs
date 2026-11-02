using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #13 (master plan Â§6.H, alpha.2). Audit-chain hash continuity:
/// three sequential <see cref="Predict{TSig}.ForwardAsync"/> calls produce three audit entries
/// whose <see cref="AuditEntry.PrevHash"/> values exactly match the predecessor's
/// <see cref="AuditEntry.EntryHash"/>. The chain head's <c>PrevHash</c> is 32 zero bytes
/// (genesis sentinel per spec Â§4.1 PSQ-2). <see cref="AuditChainVerifier"/> also confirms
/// end-to-end validity as an independent oracle.
/// </summary>
public sealed class Pareto13_AuditChainContinuity : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto13_AuditChainContinuity(ParetoTestFixture fx) => _fx = fx;

    /// <summary>Three forwards â†’ 3 audit entries; per-pair PrevHash linkage matches; verifier passes.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task ThreeForwards_AuditChain_PerPairPrevHashLinkageMatches()
    {
        var ct = TestContext.Current.CancellationToken;

        using var fake = new FakeChatClient();
        fake.EnqueueText("[[ ## Answer ## ]]\nParis\n[[ ## completed ## ]]");
        fake.EnqueueText("[[ ## Answer ## ]]\nMadrid\n[[ ## completed ## ]]");
        fake.EnqueueText("[[ ## Answer ## ]]\nLisbon\n[[ ## completed ## ]]");

        var sink = new InMemoryAuditSink();
        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new IHook[] { new AuditHook(sink) } };

        var predict = new Predict<QaSig>(ctx);
        _ = await predict.ForwardAsync(new QaSig { Question = "Capital of France?" }, ct: ct);
        _ = await predict.ForwardAsync(new QaSig { Question = "Capital of Spain?" }, ct: ct);
        _ = await predict.ForwardAsync(new QaSig { Question = "Capital of Portugal?" }, ct: ct);

        sink.NextSequence.ShouldBe(3L);

        // Snapshot the chain in order.
        var entries = new List<AuditEntry>();
        await foreach (var entry in sink.EnumerateAsync(ct)) entries.Add(entry);
        entries.Count.ShouldBe(3);

        // Sequence numbers are monotonic from 0.
        for (int i = 0; i < entries.Count; i++) entries[i].Sequence.ShouldBe((long)i);

        // Genesis: PrevHash is 32 zero bytes.
        entries[0].PrevHash.Length.ShouldBe(32);
        entries[0].PrevHash.ShouldAllBe(b => b == 0);

        // Per-pair linkage: PrevHash[i] == EntryHash[i-1].
        for (int i = 1; i < entries.Count; i++)
        {
            entries[i].PrevHash.ShouldBe(
                entries[i - 1].EntryHash,
                $"audit chain link at sequence {entries[i].Sequence} must equal hash of sequence {entries[i - 1].Sequence}");
        }

        // Independent oracle: AuditChainVerifier confirms.
        var verification = await AuditChainVerifier.VerifyAsync(sink, ct);
        verification.IsValid.ShouldBeTrue(verification.Reason);
    }
}

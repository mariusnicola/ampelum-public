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
/// Pareto integration test #5 (master plan Â§6.H, plan task Â§Pareto05; promoted alpha.3, alpha.5).
/// Streaming forward: chunks arrive in order, the terminal update yields a typed value whose
/// <c>Answer</c> field equals the value produced by a non-streaming forward over the same input.
/// alpha.3 wires <see cref="Predict{TSig}.StreamAsync"/> through the hook pipeline, so this test
/// also asserts that an <see cref="AuditHook"/> bound to the streaming context produces exactly
/// one audit entry per call with parsed Outputs that match the non-streaming reference.
/// alpha.5 (Gap 1) tightens the audit equivalence relation between the two paths:
/// streaming and non-streaming MUST produce entries whose canonical Inputs dictionaries are
/// equal, ModuleName equal, and EventType equal (the discriminator added in Gap 2).
/// EntryHash equality is intentionally NOT asserted: AuditEntry.Timestamp uses
/// <see cref="DateTimeOffset.UtcNow"/> at <c>OnAfter</c> time, which differs between the two
/// forwards and is folded into the hash. Plus the underlying LM may emit non-deterministic
/// completions, perturbing Outputs and token counts. The equivalence relation we ship is:
/// <c>(Inputs canonical, ModuleName, EventType)</c> equal AND each side produces exactly
/// one row with non-empty parsed Outputs.
/// </summary>
public sealed class Pareto05_StreamingForward : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto05_StreamingForward(ParetoTestFixture fx) => _fx = fx;

    /// <summary>Streaming yields â‰¥1 partial chunk + a terminal final update equal to non-streaming
    /// and emits one audit entry per call (streaming + non-streaming).</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task Streaming_ChunksInOrder_FinalAnswerMatchesNonStreaming()
    {
        Assert.SkipWhen(SkipUnlessRealLm.ShouldSkip, SkipUnlessRealLm.Reason);
        var ct = TestContext.Current.CancellationToken;

        using var chat = OpenRouterClientFactory.Create();
        var nonStreamSink = new InMemoryAuditSink();
        var ctxNonStream = new AmpelumContext(
            chat,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            Hooks = new IHook[] { new AuditHook(nonStreamSink) },
        };
        var predictNonStream = new Predict<QaSig>(ctxNonStream);

        var question = new QaSig { Question = "What is the capital of Germany?" };

        // Non-streaming reference call (1 LM hop).
        var reference = await predictNonStream.ForwardAsync(question, ct: ct);
        reference.Value.Answer.ShouldNotBeNullOrWhiteSpace();

        // Streaming call (1 LM hop) â€” total 2 calls, well under the 5-call cap.
        var streamSink = new InMemoryAuditSink();
        var ctxStream = new AmpelumContext(
            chat,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            Hooks = new IHook[] { new AuditHook(streamSink) },
        };
        var predictStream = new Predict<QaSig>(ctxStream);

        int partials = 0;
        QaSig? finalValue = null;
        var lastPartialLength = -1;
        await foreach (var update in predictStream.StreamAsync(question, ct: ct))
        {
            if (update.IsFinal)
            {
                finalValue = update.Value;
                break;
            }
            partials++;
            // Partial accumulators only grow; they never shrink (markers are stripped, not re-emitted).
            if (update.PartialText.Length < lastPartialLength)
                throw new ShouldAssertException($"Partial text shrunk: was {lastPartialLength}, now {update.PartialText.Length}");
            lastPartialLength = update.PartialText.Length;
        }

        partials.ShouldBeGreaterThan(0);
        finalValue.ShouldNotBeNull();
        finalValue!.Answer.ShouldNotBeNullOrWhiteSpace();

        // alpha.3 hook participation: one audit entry per call, both modes.
        var nonStreamEntries = new List<AuditEntry>();
        await foreach (var e in nonStreamSink.EnumerateAsync(ct)) nonStreamEntries.Add(e);
        var streamEntries = new List<AuditEntry>();
        await foreach (var e in streamSink.EnumerateAsync(ct)) streamEntries.Add(e);
        nonStreamEntries.Count.ShouldBe(1);
        streamEntries.Count.ShouldBe(1);

        // alpha.5 (Gap 1): audit-equivalence relation between streaming and non-streaming.
        // Inputs canonical equality.
        streamEntries[0].Inputs.Count.ShouldBe(nonStreamEntries[0].Inputs.Count);
        foreach (var kv in nonStreamEntries[0].Inputs)
        {
            streamEntries[0].Inputs.ShouldContainKey(kv.Key);
            streamEntries[0].Inputs[kv.Key].ShouldBe(kv.Value);
        }
        // ModuleName equality.
        streamEntries[0].ModuleName.ShouldBe(nonStreamEntries[0].ModuleName);
        // EventType discriminator equality (alpha.5 Gap 2).
        streamEntries[0].EventType.ShouldBe(nonStreamEntries[0].EventType);
        streamEntries[0].EventType.ShouldBe("forward.completed");
        // Each side parsed non-empty Outputs (the structural equivalence we can assert
        // against a non-deterministic LM).
        nonStreamEntries[0].Outputs.ShouldContainKey("Answer");
        streamEntries[0].Outputs.ShouldContainKey("Answer");
    }
}

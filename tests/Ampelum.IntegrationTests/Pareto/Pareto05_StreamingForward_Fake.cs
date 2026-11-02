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
/// Pareto integration test #5 â€” Fake-LM variant (alpha.10, Gap 1 closure).
/// Promotes the streamingâ†”non-streaming audit equivalence assertion from a relation
/// over <c>(Inputs canonical, ModuleName, EventType)</c> to a real hash assertion on
/// <see cref="AuditEntryReplay.GetReplayHash"/>. The real-LM Pareto05 keeps its weaker
/// equivalence relation (LM nondeterminism + skip-when-no-key); this test
/// deterministically exercises ReplayHash equality with <see cref="FakeChatClient"/>
/// so the regression gate runs in CI without an OpenRouter key.
/// </summary>
public sealed class Pareto05_StreamingForward_Fake
{
    [Fact]
    [Trait("Cost", "Free")]
    public async Task ReplayHash_Equal_BetweenStreamingAndNonStreaming_OverSameInput()
    {
        var ct = TestContext.Current.CancellationToken;
        var question = new QaSig { Question = "What is the capital of Germany?" };
        const string adapterReply = "[[ ## Answer ## ]]\nBerlin\n[[ ## completed ## ]]";

        // Non-streaming forward.
        var nonStreamSink = new InMemoryAuditSink();
        var nonStreamFake = new FakeChatClient();
        nonStreamFake.EnqueueText(adapterReply);
        var ctxNonStream = new AmpelumContext(
            nonStreamFake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            Hooks = new IHook[] { new AuditHook(nonStreamSink) },
        };
        var predictNonStream = new Predict<QaSig>(ctxNonStream);
        var nonStreamResult = await predictNonStream.ForwardAsync(question, ct: ct);
        nonStreamResult.Value.Answer.ShouldBe("Berlin");

        // Streaming forward over the same input.
        var streamSink = new InMemoryAuditSink();
        var streamFake = new FakeChatClient();
        // Streaming path consumes EnqueueStreamingChunks, not EnqueueText. One-chunk
        // emission is enough â€” the adapter joins chunks before parsing.
        streamFake.EnqueueStreamingChunks(adapterReply);
        var ctxStream = new AmpelumContext(
            streamFake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            Hooks = new IHook[] { new AuditHook(streamSink) },
        };
        var predictStream = new Predict<QaSig>(ctxStream);
        QaSig? finalValue = null;
        await foreach (var update in predictStream.StreamAsync(question, ct: ct))
        {
            if (update.IsFinal) { finalValue = update.Value; break; }
        }
        finalValue.ShouldNotBeNull();
        finalValue!.Answer.ShouldBe("Berlin");

        // Collect entries.
        var nonStreamEntries = new List<AuditEntry>();
        await foreach (var e in nonStreamSink.EnumerateAsync(ct)) nonStreamEntries.Add(e);
        var streamEntries = new List<AuditEntry>();
        await foreach (var e in streamSink.EnumerateAsync(ct)) streamEntries.Add(e);
        nonStreamEntries.Count.ShouldBe(1);
        streamEntries.Count.ShouldBe(1);

        // Gap 1 (alpha.10): ReplayHash equality is the durable equivalence assertion
        // between streaming and non-streaming forwards over the same input. EntryHash
        // diverges (Timestamp + nondeterministic Outputs/tokens fold in); ReplayHash
        // covers ONLY the input-side determinism (ModuleName, EventType, ModelId,
        // Inputs canonical), so it must be equal under any forwarding mode.
        streamEntries[0].GetReplayHash().ShouldNotBeNullOrWhiteSpace();
        streamEntries[0].GetReplayHash().ShouldBe(nonStreamEntries[0].GetReplayHash());

        // Sanity â€” the EntryHashes diverge (Timestamps differ at ms granularity).
        // The two hashes COULD coincidentally match if both forwards happened in the
        // same Tick AND tokens are identical. We don't assert inequality here to
        // avoid flake; the design doc covers when each hash is the right tool.
    }
}

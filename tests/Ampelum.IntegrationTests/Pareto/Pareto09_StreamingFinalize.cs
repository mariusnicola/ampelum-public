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
/// Pareto integration test #9 (master plan Â§6.H, alpha.2; promoted alpha.3). Streaming
/// finalize: for the same input the streaming final value equals the non-streaming value, and
/// partial chunks arrive in non-shrinking order with at least one partial before the terminal
/// update. Additionally, alpha.3 wires <see cref="Predict{TSig}.StreamAsync"/> through the hook
/// pipeline, so this test asserts that <see cref="AuditHook"/> records exactly one entry per call
/// for both modes and that streaming and non-streaming produce identical Inputs/Outputs/ModelId.
/// </summary>
/// <remarks>
/// Uses <see cref="FakeChatClient"/> for byte-level determinism â€” real LMs do not guarantee
/// streaming/non-streaming equality on the same prompt, so the equality contract is only
/// observable against a deterministic adapter+stream pair (Pareto #5 already exercises the
/// real-LM shape; this test exercises the equality clause).
/// </remarks>
public sealed class Pareto09_StreamingFinalize : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto09_StreamingFinalize(ParetoTestFixture fx) => _fx = fx;

    /// <summary>Streaming final.Answer == non-streaming Answer; chunks ordered + non-shrinking;
    /// audit entries match across modes.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task Streaming_FinalValue_MatchesNonStreaming_OverSameDeterministicResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        const string canonicalAnswer = "Lisbon";

        // Non-streaming pass â€” wire AuditHook + InMemoryAuditSink.
        using var fakeNonStream = new FakeChatClient();
        fakeNonStream.EnqueueText($"[[ ## Answer ## ]]\n{canonicalAnswer}\n[[ ## completed ## ]]");
        var nonStreamSink = new InMemoryAuditSink();
        var ctxNonStream = new AmpelumContext(
            fakeNonStream,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            Hooks = new IHook[] { new AuditHook(nonStreamSink) },
        };
        var predictNonStream = new Predict<QaSig>(ctxNonStream);
        var nonStreaming = await predictNonStream.ForwardAsync(
            new QaSig { Question = "Capital of Portugal?" }, ct: ct);
        nonStreaming.Value.Answer.ShouldBe(canonicalAnswer);

        // Streaming pass â€” same hook wiring; alpha.3 routes StreamAsync through the pipeline.
        using var fakeStream = new FakeChatClient();
        fakeStream.EnqueueStreamingOutputs(new QaSig { Question = "ignored", Answer = canonicalAnswer });
        var streamSink = new InMemoryAuditSink();
        var ctxStream = new AmpelumContext(
            fakeStream,
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
        await foreach (var update in predictStream.StreamAsync(
            new QaSig { Question = "Capital of Portugal?" }, ct: ct))
        {
            if (update.IsFinal)
            {
                finalValue = update.Value;
                break;
            }
            partials++;
            if (update.PartialText.Length < lastPartialLength)
                throw new ShouldAssertException(
                    $"Partial accumulator shrunk: was {lastPartialLength}, now {update.PartialText.Length}");
            lastPartialLength = update.PartialText.Length;
        }

        partials.ShouldBeGreaterThan(0);
        finalValue.ShouldNotBeNull();
        // The contract under test: same canonical answer regardless of mode.
        finalValue!.Answer.ShouldBe(nonStreaming.Value.Answer);
        finalValue.Answer.ShouldBe(canonicalAnswer);

        // alpha.3 hook-pipeline equality: one audit entry per call, matching shape across modes.
        var nonStreamEntries = new List<AuditEntry>();
        await foreach (var e in nonStreamSink.EnumerateAsync(ct)) nonStreamEntries.Add(e);
        var streamEntries = new List<AuditEntry>();
        await foreach (var e in streamSink.EnumerateAsync(ct)) streamEntries.Add(e);

        nonStreamEntries.Count.ShouldBe(1);
        streamEntries.Count.ShouldBe(1);

        var ns = nonStreamEntries[0];
        var st = streamEntries[0];
        st.ModuleName.ShouldBe(ns.ModuleName);
        st.Inputs["Question"].ShouldBe(ns.Inputs["Question"]);
        st.Outputs["Answer"].ShouldBe(ns.Outputs["Answer"]);
        st.Outputs["Answer"].ShouldBe(canonicalAnswer);
    }
}

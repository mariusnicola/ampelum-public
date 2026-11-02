using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Composition;

[Collection(ActivityListenerCollection.Name)]
public sealed class RefineTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static AmpelumContext Ctx(FakeChatClient chat, ITraceSink? sink = null) =>
        new(chat, new ChatAdapter(), sink ?? NullTraceSink.Instance, NullLoggerFactory.Instance);

    // ── spec tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstAttemptAboveThreshold_StopsImmediately()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\ngood");
        var metric = new UserDefined<QA>((a, _) => a.Answer == "good" ? 1.0 : 0.0);
        var pred = await new Refine<QA>(Ctx(chat), metric, threshold: 0.5)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("good");
        pred.Trace.Metadata["refinement_count"].ShouldBe(0);
        pred.Trace.Metadata["final_score"].ShouldBe(1.0);
    }

    [Fact]
    public async Task BelowThreshold_RefinesUntilMax()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nbad")
            .EnqueueText("[[ ## Answer ## ]]\nbad")
            .EnqueueText("[[ ## Answer ## ]]\nbad");
        var metric = new UserDefined<QA>((_, _) => 0.0);
        var pred = await new Refine<QA>(Ctx(chat), metric, threshold: 0.5, maxRefinements: 2)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Trace.Metadata["refinement_count"].ShouldBe(2);
    }

    sealed class FeedbackMetric : IMetric<QA>, IRefinableFeedback
    {
        public double Score(QA actual, QA expected, SignatureMetadata signature) => 0.0;
        public string GetFeedback(object actual, object expected, SignatureMetadata signature) => "add more detail";
    }

    [Fact]
    public async Task IRefinableFeedback_IsIncludedInRefinementPrompt()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nshort")
            .EnqueueText("[[ ## Answer ## ]]\nbetter");
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        await new Refine<QA>(ctx, new FeedbackMetric(), threshold: 1.0, maxRefinements: 1)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        var secondCall = sink.Records
            .Where(r => r.ModuleName == "Predict")
            .Skip(1)
            .First();
        secondCall.PromptMessages[0].Text.ShouldContain("add more detail");
    }

    // ── extra coverage tests ──────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullContext_Throws()
    {
        var metric = new UserDefined<QA>((_, _) => 1.0);
        Should.Throw<ArgumentNullException>(() => new Refine<QA>(null!, metric));
    }

    [Fact]
    public void Ctor_NullMetric_Throws()
    {
        var chat = new FakeChatClient();
        var ctx = Ctx(chat);
        Should.Throw<ArgumentNullException>(() => new Refine<QA>(ctx, null!));
    }

    [Fact]
    public async Task MaxRefinements_Zero_RunsOnce()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nonly");
        var metric = new UserDefined<QA>((_, _) => 0.0);
        var pred = await new Refine<QA>(Ctx(chat), metric, threshold: 0.5, maxRefinements: 0)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Trace.Metadata["refinement_count"].ShouldBe(0);
    }

    [Fact]
    public async Task SinkWrite_Throws_Swallowed()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\ngood");
        var sink = new ThrowingTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var metric = new UserDefined<QA>((_, _) => 1.0);
        // Should not throw despite sink throwing on Write
        var pred = await new Refine<QA>(ctx, metric, threshold: 0.5)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("good");
    }

    [Fact]
    public async Task WithNonNullDefaultChatOptions_ClonesOptions()
    {
        // Exercises the _ctx.DefaultChatOptions?.Clone() branch (non-null path).
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nlow")
            .EnqueueText("[[ ## Answer ## ]]\nbetter");
        var defaultOpts = new ChatOptions { MaxOutputTokens = 200 };
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance,
            DefaultChatOptions: defaultOpts);
        var metric = new UserDefined<QA>((_, _) => 0.0);
        var pred = await new Refine<QA>(ctx, metric, threshold: 0.5, maxRefinements: 1)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Trace.Metadata["refinement_count"].ShouldBe(1);
    }

    sealed class ThrowingTraceSink : ITraceSink
    {
        public void Write(TraceRecord record) => throw new InvalidOperationException("sink error");
    }
}

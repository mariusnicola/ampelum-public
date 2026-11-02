using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using Exceptions = SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Composition;

[Collection(ActivityListenerCollection.Name)]
public sealed class BestOfNTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static AmpelumContext Ctx(FakeChatClient chat, InMemoryTraceSink sink) =>
        new(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);

    // ── spec tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectsHighestScorer()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nLONG ANSWER HERE")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var sink = new InMemoryTraceSink();
        var lengthMetric = new UserDefined<QA>((a, _) => Math.Min(1.0, a.Answer.Length / 20.0));
        var pred = await new BestOfN<QA>(Ctx(chat, sink), lengthMetric, n: 3)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("LONG ANSWER HERE");
    }

    [Fact]
    public async Task TieBreak_LowestIndexWins()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nfirst")
            .EnqueueText("[[ ## Answer ## ]]\nsecond");
        var sink = new InMemoryTraceSink();
        var constant = new UserDefined<QA>((_, _) => 0.5);
        var pred = await new BestOfN<QA>(Ctx(chat, sink), constant, n: 2)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("first");
    }

    [Fact]
    public async Task AllFail_RethrowsFirst()
    {
        var chat = new FakeChatClient()
            .EnqueueError(new InvalidOperationException("first"))
            .EnqueueError(new InvalidOperationException("second"));
        var sink = new InMemoryTraceSink();
        var metric = new UserDefined<QA>((_, _) => 1.0);
        var ex = await Should.ThrowAsync<Exceptions.LmCallException>(() =>
            new BestOfN<QA>(Ctx(chat, sink), metric, n: 2).ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Trace_HasCandidateScores()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var sink = new InMemoryTraceSink();
        var metric = new UserDefined<QA>((_, _) => 0.5);
        await new BestOfN<QA>(Ctx(chat, sink), metric, n: 2).ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        var rec = sink.Records.Single(r => r.ModuleName == "BestOfN");
        ((IReadOnlyList<double>)rec.Metadata["candidate_scores"]!).Count.ShouldBe(2);
    }

    // ── extra coverage tests ──────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullContext_Throws()
    {
        var metric = new UserDefined<QA>((_, _) => 1.0);
        Should.Throw<ArgumentNullException>(() => new BestOfN<QA>(null!, metric, 2));
    }

    [Fact]
    public void Ctor_NullMetric_Throws()
    {
        var chat = new FakeChatClient();
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        Should.Throw<ArgumentNullException>(() => new BestOfN<QA>(ctx, null!, 2));
    }

    [Fact]
    public void Ctor_N_LessThanTwo_Throws()
    {
        var chat = new FakeChatClient();
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var metric = new UserDefined<QA>((_, _) => 1.0);
        var ex = Should.Throw<ArgumentOutOfRangeException>(() => new BestOfN<QA>(ctx, metric, 1));
        ex.ParamName.ShouldBe("n");
    }

    [Fact]
    public async Task SinkWrite_Throws_Swallowed()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var sink = new ThrowingTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var metric = new UserDefined<QA>((_, _) => 1.0);
        // Should not throw despite sink throwing
        var pred = await new BestOfN<QA>(ctx, metric, n: 2).ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("a");
    }

    sealed class ThrowingTraceSink : ITraceSink
    {
        public void Write(TraceRecord record) => throw new InvalidOperationException("sink error");
    }
}

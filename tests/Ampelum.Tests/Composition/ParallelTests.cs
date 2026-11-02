using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Composition;

[Collection(ActivityListenerCollection.Name)]
public sealed class ParallelTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    sealed record Scored : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public double Score { get; init; }
    }

    sealed record ScoredInt : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public int Count { get; init; }
    }

    sealed record ScoredLong : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public long Value { get; init; }
    }

    sealed record ScoredFloat : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public float Rating { get; init; }
    }

    sealed record Mixed : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string Text { get; init; } = "";
        [Output] public int Num { get; init; }
    }

    static AmpelumContext Ctx(FakeChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    // ── spec tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StringOutputs_ConcatWithPipe()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## Answer ## ]]\nlondon");
        var mods = new[] { new Predict<QA>(Ctx(chat)), new Predict<QA>(Ctx(chat)) };
        var pred = await new Parallel<QA>(Ctx(chat), mods)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris | london");
    }

    [Fact]
    public async Task NumericOutputs_Averaged()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Score ## ]]\n1.0")
            .EnqueueText("[[ ## Score ## ]]\n3.0");
        var mods = new[] { new Predict<Scored>(Ctx(chat)), new Predict<Scored>(Ctx(chat)) };
        var pred = await new Parallel<Scored>(Ctx(chat), mods)
            .ForwardAsync(new Scored { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Score.ShouldBe(2.0);
    }

    [Fact]
    public async Task EmitsOneTrace_WithModuleCount()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var mods = new[] { new Predict<QA>(ctx), new Predict<QA>(ctx) };
        await new Parallel<QA>(ctx, mods)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        sink.Records.Single(r => r.ModuleName == "Parallel").Metadata["module_count"].ShouldBe(2);
    }

    // ── extra coverage tests: constructor validation ──────────────────────────

    [Fact]
    public void Ctor_NullContext_Throws()
    {
        var mods = new[] { new Predict<QA>(Ctx(new FakeChatClient())) };
        Should.Throw<ArgumentNullException>(() => new Parallel<QA>(null!, mods));
    }

    [Fact]
    public void Ctor_NullModules_Throws()
    {
        var chat = new FakeChatClient();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        Should.Throw<ArgumentNullException>(() => new Parallel<QA>(ctx, null!));
    }

    [Fact]
    public void Ctor_EmptyModules_Throws()
    {
        var chat = new FakeChatClient();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var ex = Should.Throw<ArgumentException>(() => new Parallel<QA>(ctx, Array.Empty<Predict<QA>>()));
        ex.ParamName.ShouldBe("modules");
    }

    // ── extra coverage tests: numeric types ───────────────────────────────────

    [Fact]
    public async Task IntOutputs_Averaged_CastToInt()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Count ## ]]\n1")
            .EnqueueText("[[ ## Count ## ]]\n3");
        var mods = new[] { new Predict<ScoredInt>(Ctx(chat)), new Predict<ScoredInt>(Ctx(chat)) };
        var pred = await new Parallel<ScoredInt>(Ctx(chat), mods)
            .ForwardAsync(new ScoredInt { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Count.ShouldBe(2); // (1 + 3) / 2 = 2.0 → 2
    }

    [Fact]
    public async Task LongOutputs_Averaged_CastToLong()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Value ## ]]\n10")
            .EnqueueText("[[ ## Value ## ]]\n20");
        var mods = new[] { new Predict<ScoredLong>(Ctx(chat)), new Predict<ScoredLong>(Ctx(chat)) };
        var pred = await new Parallel<ScoredLong>(Ctx(chat), mods)
            .ForwardAsync(new ScoredLong { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Value.ShouldBe(15L); // (10 + 20) / 2 = 15.0 → 15L
    }

    [Fact]
    public async Task FloatOutputs_Averaged_CastToFloat()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Rating ## ]]\n1.5")
            .EnqueueText("[[ ## Rating ## ]]\n2.5");
        var mods = new[] { new Predict<ScoredFloat>(Ctx(chat)), new Predict<ScoredFloat>(Ctx(chat)) };
        var pred = await new Parallel<ScoredFloat>(Ctx(chat), mods)
            .ForwardAsync(new ScoredFloat { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Rating.ShouldBe(2.0f); // (1.5 + 2.5) / 2 = 2.0f
    }


    // ── extra coverage tests: fallback for non-numeric types ─────────────────

    [Fact]
    public async Task NonStringNonNumericType_FallsBackToFirstModule()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Text ## ]]\nfirst\n[[ ## Num ## ]]\n10")
            .EnqueueText("[[ ## Text ## ]]\nsecond\n[[ ## Num ## ]]\n20");
        var mods = new[] { new Predict<Mixed>(Ctx(chat)), new Predict<Mixed>(Ctx(chat)) };
        var pred = await new Parallel<Mixed>(Ctx(chat), mods)
            .ForwardAsync(new Mixed { Q = "?" }, ct: TestContext.Current.CancellationToken);
        // Text is string: concat
        pred.Value.Text.ShouldBe("first | second");
        // Num is int: average
        pred.Value.Num.ShouldBe(15); // (10 + 20) / 2 = 15
    }

    // ── extra coverage tests: trace + error handling ───────────────────────────

    [Fact]
    public async Task SinkWrite_Throws_Swallowed()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\na")
            .EnqueueText("[[ ## Answer ## ]]\nb");
        var sink = new ThrowingTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var mods = new[] { new Predict<QA>(ctx), new Predict<QA>(ctx) };
        // Should not throw despite sink throwing
        var pred = await new Parallel<QA>(ctx, mods)
            .ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("a | b");
    }

    sealed class ThrowingTraceSink : ITraceSink
    {
        public void Write(TraceRecord record) => throw new InvalidOperationException("sink error");
    }
}

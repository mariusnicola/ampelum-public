using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Composition;

[Collection(ActivityListenerCollection.Name)]
public sealed class MultiChainComparisonTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    sealed record QANullable : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string? Answer { get; init; }
    }

    static AmpelumContext Ctx(FakeChatClient chat, InMemoryTraceSink sink) =>
        new(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);

    // ── spec tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MajorityVote_PicksMostCommonAnswer()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr3\n[[ ## Answer ## ]]\nlondon");
        // Aggregation LM call not used for MajorityVote (decided locally) but the spec's flow
        // says always one aggregation call — enqueue a passthrough anyway.
        chat.EnqueueText("[[ ## reasoning ## ]]\n\n[[ ## Answer ## ]]\nparis");
        var sink = new InMemoryTraceSink();
        var m = new MultiChainComparison<QA>(Ctx(chat, sink), n: 3, AggregatorStrategy.MajorityVote);
        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris");
    }

    [Fact]
    public async Task ModelSynthesize_EmitsOneTrace_WithSubTracesMetadata()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr3\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var sink = new InMemoryTraceSink();
        var m = new MultiChainComparison<QA>(Ctx(chat, sink), n: 3, AggregatorStrategy.ModelSynthesize);
        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);

        // Three inner CoT traces (via Predict via augmented adapter) + one MultiChainComparison trace.
        sink.Records.Count(r => r.ModuleName == "MultiChainComparison").ShouldBe(1);
        var mcc = sink.Records.Single(r => r.ModuleName == "MultiChainComparison");
        mcc.Metadata.ContainsKey("sub_traces").ShouldBeTrue();
        ((System.Collections.IEnumerable)mcc.Metadata["sub_traces"]!).Cast<TraceRecord>().Count().ShouldBe(3);
    }

    [Fact]
    public async Task Cancellation_PropagatesToAllCalls()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var chat = new FakeChatClient(); // no responses enqueued; will error out anyway
        var sink = new InMemoryTraceSink();
        var m = new MultiChainComparison<QA>(Ctx(chat, sink), n: 3);
        await Should.ThrowAsync<OperationCanceledException>(() =>
            m.ForwardAsync(new QA { Question = "x" }, ct: cts.Token));
    }

    // ── extra coverage tests ──────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullContext_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new MultiChainComparison<QA>(null!));
    }

    [Fact]
    public void Ctor_N_LessThan_2_Throws()
    {
        var chat = new FakeChatClient();
        var sink = new InMemoryTraceSink();
        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            new MultiChainComparison<QA>(Ctx(chat, sink), n: 1));
        ex.ParamName.ShouldBe("n");
    }

    [Fact]
    public async Task ModelSelect_Strategy_Works()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nlondon")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var sink = new InMemoryTraceSink();
        var m = new MultiChainComparison<QA>(Ctx(chat, sink), n: 2, AggregatorStrategy.ModelSelect);
        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris");

        // Verify the aggregation call contains the ModelSelect instruction.
        var aggCall = chat.RecordedCalls[2];
        aggCall.Messages.ShouldContain(msg =>
            msg.Text != null && msg.Text.Contains("Select the single best answer from the candidates below."));
    }

    [Fact]
    public async Task MajorityVote_MinorityAnswer_IsNotPicked()
    {
        // 2 votes for "berlin", 1 for "rome" — majority wins.
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nberlin")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nrome")
            .EnqueueText("[[ ## reasoning ## ]]\nr3\n[[ ## Answer ## ]]\nberlin")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nberlin");
        var sink = new InMemoryTraceSink();
        var m = new MultiChainComparison<QA>(Ctx(chat, sink), n: 3, AggregatorStrategy.MajorityVote);
        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("berlin");
    }

    [Fact]
    public async Task BuildAggregationPrompt_NullOutputValue_UsesEmptyString()
    {
        // QANullable has a nullable Answer; if the LM returns no Answer field,
        // RecordCloner sets it to null, exercising the `?.ToString() ?? ""` branch in BuildAggregationPrompt.
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\n")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\n")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var m = new MultiChainComparison<QANullable>(ctx, n: 2, AggregatorStrategy.ModelSynthesize);
        // Should complete without error even when output values are empty/null.
        var pred = await m.ForwardAsync(new QANullable { Question = "cap?" }, ct: TestContext.Current.CancellationToken);
        pred.ShouldNotBeNull();
    }

    [Fact]
    public async Task Sink_Write_Throws_IsSwallowed()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var throwingSink = new ThrowingTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), throwingSink, NullLoggerFactory.Instance);
        var m = new MultiChainComparison<QA>(ctx, n: 2, AggregatorStrategy.ModelSynthesize);

        // ForwardAsync must complete without re-throwing the sink exception.
        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris");
        throwingSink.WasThrown.ShouldBeTrue();
    }

    [Fact]
    public async Task ForwardAsync_WithTrulyAsyncChatClient_CoversAsyncSuspensionBranches()
    {
        // Use a chat client that returns genuinely incomplete tasks to trigger
        // async state machine suspension branches in RunAggregatorCall and RunModelAggregator.
        var chat = new TrulyAsyncChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var m = new MultiChainComparison<QA>(ctx, n: 2, AggregatorStrategy.ModelSynthesize);
        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris");
    }

    [Fact]
    public async Task ForwardAsync_WithActivityListener_CoversActivityNonNullBranches()
    {
        // Register an ActivityListener so Diagnostics.Source.StartActivity returns a non-null Activity,
        // exercising all activity?.SetTag(...) non-null branches in ForwardAsync.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var sink = new InMemoryTraceSink();
        var m = new MultiChainComparison<QA>(Ctx(chat, sink), n: 2, AggregatorStrategy.ModelSynthesize);
        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("paris");
    }

    [Fact]
    public async Task AggregationCall_RoutesThroughHooks()
    {
        // Both RunAggregatorCall (MajorityVote) and RunModelAggregator (ModelSynthesize) run
        // through HookedLmCall now; verify a hook on the context fires for the aggregation call.
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## reasoning ## ]]\nr1\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nr2\n[[ ## Answer ## ]]\nparis")
            .EnqueueText("[[ ## reasoning ## ]]\nagg\n[[ ## Answer ## ]]\nparis");
        var sink = new InMemoryTraceSink();
        var hook = new SpyHook();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance) { Hooks = [hook] };
        var m = new MultiChainComparison<QA>(ctx, n: 2, AggregatorStrategy.ModelSynthesize);

        var pred = await m.ForwardAsync(new QA { Question = "cap?" }, ct: TestContext.Current.CancellationToken);

        pred.Value.Answer.ShouldBe("paris");
        // 3 hook-firing LM calls total: 2 inner ChainOfThought calls (via Predict, which already
        // uses the hook pipeline) + 1 MultiChainComparison aggregation call.
        hook.BeforeCount.ShouldBe(3);
        hook.AfterCount.ShouldBe(3);
    }

    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public int BeforeCount { get; private set; }
        public int AfterCount { get; private set; }

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            BeforeCount++;
            return default;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            AfterCount++;
            return default;
        }

        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="IChatClient"/> that introduces a real async yield before each response,
    /// causing the async state machine to suspend (covers the async-suspension branch).
    /// </summary>
    sealed class TrulyAsyncChatClient : IChatClient
    {
        readonly System.Collections.Concurrent.ConcurrentQueue<string> _queue = new();
        public ChatClientMetadata Metadata { get; } = new(nameof(TrulyAsyncChatClient));

        public TrulyAsyncChatClient EnqueueText(string text)
        {
            _queue.Enqueue(text);
            return this;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield(); // force genuine async suspension
            if (!_queue.TryDequeue(out var text))
                throw new InvalidOperationException("TrulyAsyncChatClient: no enqueued response.");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>A <see cref="ITraceSink"/> that throws on every <see cref="Write"/> call.</summary>
    sealed class ThrowingTraceSink : ITraceSink
    {
        public bool WasThrown { get; private set; }

        public void Write(TraceRecord record)
        {
            WasThrown = true;
            throw new InvalidOperationException("Sink intentionally broken.");
        }
    }
}

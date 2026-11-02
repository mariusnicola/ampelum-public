using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class DynamicPredictTests
{
    static AmpelumContext Ctx(IChatClient chat, ITraceSink? sink = null) =>
        new(chat, new ChatAdapter(), sink ?? new InMemoryTraceSink(), NullLoggerFactory.Instance);

    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = new();

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        {
            Calls.Add($"error:{error.GetType().Name}");
            return default;
        }
    }

    [Fact]
    public async Task Forward_RunsThroughHookedLmCall_HooksFireAroundTheLmCall()
    {
        var meta = Signature.Parse("question -> answer");
        var chat = new FakeChatClient().EnqueueText("[[ ## answer ## ]]\nParis");
        var hook = new SpyHook();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), new InMemoryTraceSink(), NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };
        var module = new DynamicPredict(ctx);

        var result = await module.ForwardAsync(
            meta,
            new Dictionary<string, object?> { ["question"] = "Capital of France?" },
            ct: TestContext.Current.CancellationToken);

        result.Value["answer"].ShouldBe("Paris");
        hook.Calls.ShouldBe(new[] { "before:DynamicPredict.Forward", "after:DynamicPredict.Forward" });
    }

    [Fact]
    public async Task Forward_ParsesAdapterResponse_IntoDictionary()
    {
        var meta = Signature.Parse("question -> answer");
        var chat = new FakeChatClient().EnqueueText("[[ ## answer ## ]]\nParis");
        var sink = new InMemoryTraceSink();
        var module = new DynamicPredict(Ctx(chat, sink));
        var result = await module.ForwardAsync(
            meta,
            new Dictionary<string, object?> { ["question"] = "Capital of France?" },
            ct: TestContext.Current.CancellationToken);

        result.Value["answer"].ShouldBe("Paris");
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].ModuleName.ShouldBe("DynamicPredict");
        sink.Records[0].SignatureType.ShouldBe(typeof(Dictionary<string, object?>));
    }

    [Fact]
    public async Task Forward_MissingInputKey_ThrowsAdapterFormatException_NoLmCall()
    {
        var meta = Signature.Parse("q1, q2 -> a");
        var chat = new FakeChatClient();   // queue empty — proves no LM call
        var module = new DynamicPredict(Ctx(chat));
        await Should.ThrowAsync<AdapterFormatException>(() =>
            module.ForwardAsync(meta, new Dictionary<string, object?> { ["q1"] = "x" }));
        chat.RecordedCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Forward_ExtraInputKey_IsSilentlyIgnored()
    {
        var meta = Signature.Parse("q -> a");
        var chat = new FakeChatClient().EnqueueText("[[ ## a ## ]]\nok");
        var module = new DynamicPredict(Ctx(chat));
        var result = await module.ForwardAsync(meta,
            new Dictionary<string, object?> { ["q"] = "?", ["extra"] = "ignored" },
            ct: TestContext.Current.CancellationToken);
        result.Value["a"].ShouldBe("ok");
    }

    [Fact] public void Create_Factory_WiresDefaults()
    {
        var module = DynamicPredict.Create(new FakeChatClient());
        module.ShouldNotBeNull();
    }

    [Fact] public void Ctor_NullContext_Throws() =>
        Should.Throw<ArgumentNullException>(() => new DynamicPredict(null!));

    [Fact]
    public async Task Forward_AdapterParseFailure_WritesErrorTrace_AndRethrows()
    {
        var meta = Signature.Parse("q -> a");
        var chat = new FakeChatClient().EnqueueText("garbage with no marker");
        var sink = new InMemoryTraceSink();
        var module = new DynamicPredict(Ctx(chat, sink));
        await Should.ThrowAsync<AdapterParseException>(() =>
            module.ForwardAsync(meta, new Dictionary<string, object?> { ["q"] = "?" }));
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldNotBeNull();
        sink.Records[0].Error.ShouldBeOfType<AdapterParseException>();
        sink.Records[0].ModuleName.ShouldBe("DynamicPredict");
    }

    [Fact]
    public async Task Forward_WithActivityListener_CoversActivityNonNullBranches()
    {
        // Installs an ActivitySource listener so activity is non-null, covering all activity?.XYZ branches.
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var meta = Signature.Parse("q -> a");
        var chat = new FakeChatClient().EnqueueText("garbage with no marker");
        var sink = new InMemoryTraceSink();
        var module = new DynamicPredict(Ctx(chat, sink));
        // Parse fails → exercises activity?.SetStatus in the catch path with activity non-null.
        await Should.ThrowAsync<AdapterParseException>(() =>
            module.ForwardAsync(meta, new Dictionary<string, object?> { ["q"] = "?" },
                ct: TestContext.Current.CancellationToken));
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<AdapterParseException>();
    }

    [Fact]
    public async Task Forward_LmThrows_ResponseIsNull_InErrorTrace()
    {
        // Enqueues an error so GetResponseAsync throws → response stays null in catch.
        // Covers response?.Text ?? "" (null side) and response?.Usage (null side).
        var meta = Signature.Parse("q -> a");
        var chat = new FakeChatClient().EnqueueError(new InvalidOperationException("LM is down"));
        var sink = new InMemoryTraceSink();
        var module = new DynamicPredict(Ctx(chat, sink));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            module.ForwardAsync(meta, new Dictionary<string, object?> { ["q"] = "?" },
                ct: TestContext.Current.CancellationToken));
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<InvalidOperationException>();
        sink.Records[0].RawResponse.ShouldBe("");
        sink.Records[0].Usage.ShouldBeNull();
    }

    sealed class ThrowingSink : ITraceSink
    {
        public int Calls { get; private set; }
        public void Write(TraceRecord record)
        {
            Calls++;
            if (Calls == 1) throw new InvalidOperationException("sink boom");
        }
    }

    [Fact]
    public async Task Forward_SinkThrowsOnSuccessWrite_FallsToCatch_OutputsNonNull()
    {
        // TraceSink.Write throws on the success path → outputs is non-null in catch.
        // Covers the outputs != null branch of `outputs ?? new Dictionary<>()`.
        var meta = Signature.Parse("q -> a");
        var chat = new FakeChatClient().EnqueueText("[[ ## a ## ]]\nok");
        var sink = new ThrowingSink();
        var module = new DynamicPredict(new AmpelumContext(chat, new ChatAdapter(), sink, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            module.ForwardAsync(meta, new Dictionary<string, object?> { ["q"] = "?" },
                ct: TestContext.Current.CancellationToken));
        ex.Message.ShouldBe("sink boom");
        // ThrowingSink.Calls == 2: first call (success path) throws; catch writes a second trace.
        sink.Calls.ShouldBe(2);
    }
}

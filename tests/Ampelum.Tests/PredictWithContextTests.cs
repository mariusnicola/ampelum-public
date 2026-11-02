using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class PredictWithContextTests
{
    static AmpelumContext MakeCtx(string? submodule = null) =>
        new AmpelumContext(
            new FakeChatClient(),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        { SubmoduleName = submodule };

    [Signature("Summarise text.")]
    sealed record SumSig : ISignature
    {
        [Input] public required string Text { get; init; }
        [Output] public string Summary { get; init; } = "";
    }

    [Fact]
    public void WithContext_ReturnsNewInstance()
    {
        var original = new Predict<SumSig>(MakeCtx());
        var rebound  = original.WithContext(MakeCtx("predict0"));
        ReferenceEquals(original, rebound).ShouldBeFalse();
    }

    [Fact]
    public void WithContext_NewInstanceIsDecoupledFromOriginal()
    {
        var ctx1 = MakeCtx();
        var ctx2 = MakeCtx("predict0") with
        {
            DefaultDemos = new List<Example>
            {
                new(new Dictionary<string, object?> { ["Text"] = "x" },
                    new Dictionary<string, object?> { ["Summary"] = "y" })
            }
        };
        var original = new Predict<SumSig>(ctx1);
        var rebound  = original.WithContext(ctx2);
        rebound.ShouldBeOfType<Predict<SumSig>>();
    }

    [Fact]
    public void WithContext_NullContext_Throws()
    {
        var predict = new Predict<SumSig>(MakeCtx());
        Should.Throw<ArgumentNullException>(() => predict.WithContext(null!));
    }
}

public sealed class PredictForwardRuntimeTests
{
    [Signature("Answer the question.")]
    sealed record QASig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    static string FormatChatResponse(string answer) =>
        $"[[ ## Answer ## ]]\n{answer}\n[[ ## completed ## ]]";

    static AmpelumContext MakeCtx(
        FakeChatClient chat,
        string? submodule = null,
        IReadOnlyList<Example>? defaultDemos = null,
        IReadOnlyDictionary<string, string>? overrides = null) =>
        new AmpelumContext(
            chat,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
        {
            SubmoduleName        = submodule,
            DefaultDemos         = defaultDemos,
            InstructionOverrides = overrides
        };

    static Example MakeDemo(string q, string a) =>
        new(new Dictionary<string, object?> { ["Question"] = q },
            new Dictionary<string, object?> { ["Answer"]   = a });

    [Fact]
    public async Task ForwardAsync_UsesDefaultDemos_WhenCallerPassesNull()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("42"));
        var defaultDemos = new List<Example> { MakeDemo("1+1", "2") };
        var ctx = MakeCtx(chat, defaultDemos: defaultDemos);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(new QASig { Question = "6x7" }, demos: null,
            ct: TestContext.Current.CancellationToken);

        result.Trace.Metadata.ShouldContainKey(TraceKeys.DemosUsed);
        var used = result.Trace.Metadata[TraceKeys.DemosUsed].ShouldBeAssignableTo<IReadOnlyList<Example>>();
        used!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ForwardAsync_ExplicitDemos_OverrideDefaultDemos()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("ok"));
        var defaultDemos  = new List<Example> { MakeDemo("1+1", "2") };
        var explicitDemos = new List<Example> { MakeDemo("2+2", "4"), MakeDemo("3+3", "6") };
        var ctx = MakeCtx(chat, defaultDemos: defaultDemos);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(
            new QASig { Question = "q" }, demos: explicitDemos,
            ct: TestContext.Current.CancellationToken);

        var used = result.Trace.Metadata[TraceKeys.DemosUsed].ShouldBeAssignableTo<IReadOnlyList<Example>>();
        used!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ForwardAsync_AppliesInstructionOverride_WhenSubmoduleNameMatches()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("overridden"));
        var overrides = new Dictionary<string, string> { ["predict0"] = "CUSTOM INSTRUCTIONS" };
        var ctx = MakeCtx(chat, submodule: "predict0", overrides: overrides);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(new QASig { Question = "q" },
            ct: TestContext.Current.CancellationToken);

        result.Trace.PromptMessages
            .Any(m => m.Text?.Contains("CUSTOM INSTRUCTIONS") == true)
            .ShouldBeTrue();
        result.Trace.PromptMessages
            .All(m => m.Text?.Contains("Answer the question.") != true)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task ForwardAsync_DoesNotApplyOverride_WhenSubmoduleNameIsNull()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("natural"));
        var overrides = new Dictionary<string, string> { ["predict0"] = "CUSTOM INSTRUCTIONS" };
        var ctx = MakeCtx(chat, submodule: null, overrides: overrides);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(new QASig { Question = "q" },
            ct: TestContext.Current.CancellationToken);

        result.Trace.PromptMessages
            .Any(m => m.Text?.Contains("Answer the question.") == true)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task ForwardAsync_DoesNotApplyOverride_WhenOverridesIsNull()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("natural"));
        var ctx = MakeCtx(chat, submodule: "predict0", overrides: null);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(new QASig { Question = "q" },
            ct: TestContext.Current.CancellationToken);

        result.Trace.PromptMessages
            .Any(m => m.Text?.Contains("Answer the question.") == true)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task ForwardAsync_DemosUsed_NotWritten_WhenDemosEmpty()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("empty"));
        var ctx = MakeCtx(chat);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(
            new QASig { Question = "q" }, demos: null,
            ct: TestContext.Current.CancellationToken);

        result.Trace.Metadata.ContainsKey(TraceKeys.DemosUsed).ShouldBeFalse();
    }

    [Fact]
    public async Task ForwardAsync_DoesNotApplyOverride_WhenSubmoduleNameNotInDict()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("natural"));
        var overrides = new Dictionary<string, string> { ["predict0"] = "OTHER INSTR" };
        var ctx = MakeCtx(chat, submodule: "predict1", overrides: overrides);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(new QASig { Question = "q" },
            ct: TestContext.Current.CancellationToken);

        result.Trace.PromptMessages
            .Any(m => m.Text?.Contains("Answer the question.") == true)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task ForwardAsync_CallerEmptyDemos_FallsBackToDefault()
    {
        var chat = new FakeChatClient();
        chat.EnqueueText(FormatChatResponse("ok"));
        var defaultDemos = new List<Example> { MakeDemo("capital of France", "Paris") };
        var ctx = MakeCtx(chat, defaultDemos: defaultDemos);
        var predict = new Predict<QASig>(ctx);

        var result = await predict.ForwardAsync(
            new QASig { Question = "q" }, demos: new List<Example>(),
            ct: TestContext.Current.CancellationToken);

        var used = result.Trace.Metadata[TraceKeys.DemosUsed].ShouldBeAssignableTo<IReadOnlyList<Example>>();
        used!.Count.ShouldBe(1);
    }
}

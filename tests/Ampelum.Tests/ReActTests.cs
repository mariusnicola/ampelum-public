using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using System.Diagnostics;

[Collection(ActivityListenerCollection.Name)]
public sealed class ReActTests
{
    [Signature("")] public sealed record QA : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("")] public sealed record TwoOut : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
        [Output] public string B { get; init; } = "";
    }

    [Signature("")] public sealed record IntOut : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public int N { get; init; }
    }

    [Signature("Produce a greeting.")] public sealed record GreetOut : ISignature
    {
        [Input]  public required string Name { get; init; }
        [Output] public string Greeting { get; init; } = "";
    }

    // Uses JSON deserialization path (non-primitive, non-string output).
    [Signature("")] public sealed record IntArrayOut : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public int[] Items { get; init; } = [];
    }

static AmpelumContext Ctx(FakeChatClient chat, ITraceSink? sink = null) =>
        new(chat, new ChatAdapter(), sink ?? NullTraceSink.Instance, NullLoggerFactory.Instance);

    static AIFunction EchoTool() => AIFunctionFactory.Create((string input) => $"echoed:{input}", "echo", "Echo a string.");

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
    public async Task Forward_RunsThroughHookedLmCall_HooksFireAroundEachLmCall()
    {
        var chat = new FakeChatClient()
            .EnqueueText("Thought: I know it.\nfinal_answer: Paris");
        var hook = new SpyHook();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };
        var react = new ReAct<QA>(ctx, tools: [EchoTool()]);

        var pred = await react.ForwardAsync(new QA { Question = "Capital of France?" }, ct: TestContext.Current.CancellationToken);

        pred.Value.Answer.ShouldBe("Paris");
        hook.Calls.ShouldBe(new[] { "before:ReAct.Reason", "after:ReAct.Reason" });
    }

    [Fact]
    public async Task Forward_ForcedFinalCall_RunsThroughHookedLmCall_WithDistinctModuleName()
    {
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "echo",
            new Dictionary<string, object?> { ["input"] = "x" }));
        var chat = new FakeChatClient();
        chat.EnqueueRaw(new ChatResponse(toolMsg));
        chat.EnqueueText("final_answer: forced");
        var hook = new SpyHook();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };
        var react = new ReAct<QA>(ctx, tools: [EchoTool()], maxIterations: 1);

        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);

        pred.Value.Answer.ShouldBe("forced");
        hook.Calls.ShouldBe(new[]
        {
            "before:ReAct.Reason", "after:ReAct.Reason",
            "before:ReAct.ForceFinalAnswer", "after:ReAct.ForceFinalAnswer",
        });
    }

    // â”€â”€ Spec tests (verbatim from task brief) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact] public void Ctor_MultiOutputSignature_Throws()
    {
        var chat = new FakeChatClient();
        Should.Throw<SignatureDefinitionException>(() =>
            new ReAct<TwoOut>(Ctx(chat), tools: [EchoTool()]));
    }

    [Fact]
    public async Task Forward_DirectFinalAnswer_NoToolCall()
    {
        var chat = new FakeChatClient()
            .EnqueueText("Thought: I know it.\nfinal_answer: Paris");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [EchoTool()]);
        var pred = await react.ForwardAsync(new QA { Question = "Capital of France?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("Paris");
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].ModuleName.ShouldBe("ReAct");
        sink.Records[0].Metadata.ShouldContainKey(TraceKeys.ToolCalls);
        ((IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!).Count.ShouldBe(0);
    }

    [Fact]
    public async Task Forward_OneToolCall_ThenFinalAnswer()
    {
        var firstMsg = new ChatMessage(ChatRole.Assistant, "");
        firstMsg.Contents.Add(new FunctionCallContent("c1", "echo",
            new Dictionary<string, object?> { ["input"] = "hello" }));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(firstMsg))
            .EnqueueText("final_answer: echoed:hello");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [EchoTool()]);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("echoed:hello");
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].FunctionName.ShouldBe("echo");
        toolCalls[0].Result!.ToString().ShouldBe("echoed:hello");
    }

    [Fact]
    public async Task Forward_MaxIterations_RetriesWithoutTools_Then_TextOnlyResponseSucceeds()
    {
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "echo",
            new Dictionary<string, object?> { ["input"] = "x" }));
        var chat = new FakeChatClient();
        for (var i = 0; i < 3; i++)
            chat.EnqueueRaw(new ChatResponse(toolMsg));
        chat.EnqueueText("final_answer: forced");
        var react = new ReAct<QA>(Ctx(chat), tools: [EchoTool()], maxIterations: 3);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("forced");
        chat.RecordedCalls.Count.ShouldBe(4);
        chat.RecordedCalls[^1].Options!.Tools.ShouldBeNull();
    }

    [Fact]
    public async Task Forward_MaxIterationsExhaustedAndFinalStillFailsParse_ThrowsReActLimit()
    {
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "echo",
            new Dictionary<string, object?> { ["input"] = "x" }));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueText("no marker no final answer");
        var react = new ReAct<QA>(Ctx(chat), tools: [EchoTool()], maxIterations: 2);
        var ex = await Should.ThrowAsync<ReActIterationLimitException>(() =>
            react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));
        ex.MaxIterations.ShouldBe(2);
    }

    [Fact]
    public async Task Forward_EmitsToolCallChildActivity()
    {
        var captured = new List<Activity>();
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            // Filter to tool_call activities only to avoid parallel-test interference
            // with other listeners that capture all Ampelum activities.
            ActivityStopped = a =>
            {
                if (a.TraceId != parentTraceId) return;
                if (a.OperationName == "SignalGrid.Ampelum.react.tool_call") captured.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "echo",
            new Dictionary<string, object?> { ["input"] = "hi" }));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueText("final_answer: done");
        var react = new ReAct<QA>(Ctx(chat), tools: [EchoTool()]);
        await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        captured.ShouldContain(a => a.OperationName == "SignalGrid.Ampelum.react.tool_call");
    }

    // â”€â”€ Additional coverage tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Forward_WithDemos_RendersAsUserAssistantPairs()
    {
        var chat = new FakeChatClient()
            .EnqueueText("final_answer: Rome");
        var react = new ReAct<QA>(Ctx(chat), tools: [EchoTool()]);
        var demos = new List<Example>
        {
            new(Inputs: new Dictionary<string, object?> { ["Question"] = "Capital of France?" },
                Outputs: new Dictionary<string, object?> { ["Answer"] = "Paris" })
        };
        var pred = await react.ForwardAsync(new QA { Question = "Capital of Italy?" }, demos, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("Rome");
        // 4 messages: system + demo user + demo assistant + current user
        chat.RecordedCalls[0].Messages.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Forward_WithDefaultChatOptions_ClonesAndMergesTools()
    {
        var chat = new FakeChatClient()
            .EnqueueText("final_answer: ok");
        var defaultOpts = new ChatOptions { MaxOutputTokens = 100 };
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance, defaultOpts);
        var react = new ReAct<QA>(ctx, tools: [EchoTool()]);
        await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        var recorded = chat.RecordedCalls[0].Options!;
        recorded.MaxOutputTokens.ShouldBe(100);
        recorded.Tools.ShouldNotBeNull();
        recorded.Tools!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Forward_UnknownToolName_ThrowsAdapterParseException()
    {
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "nonexistent",
            new Dictionary<string, object?> { ["input"] = "x" }));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg));
        var react = new ReAct<QA>(Ctx(chat), tools: [EchoTool()]);
        var ex = await Should.ThrowAsync<AdapterParseException>(() =>
            react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("nonexistent");
    }

    [Fact]
    public async Task Forward_ToolThrows_RecordsFailedToolCallAndContinues()
    {
        // A tool that throws; ReAct records it and feeds the error back as tool result.
        var throwingTool = AIFunctionFactory.Create(
            (string input) => ThrowAndReturn(),
            "thrower", "A tool that always throws.");
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "thrower",
            new Dictionary<string, object?> { ["input"] = "x" }));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueText("final_answer: recovered");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [throwingTool]);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("recovered");
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].Error.ShouldNotBeNull();
        toolCalls[0].Result.ShouldBeNull();
    }

    static string ThrowAndReturn() => throw new InvalidOperationException("boom");

    [Fact]
    public async Task Forward_IntOutput_Success()
    {
        var chat = new FakeChatClient()
            .EnqueueText("final_answer: 42");
        var react = new ReAct<IntOut>(Ctx(chat), tools: [EchoTool()]);
        var pred = await react.ForwardAsync(new IntOut { Q = "What is 6*7?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.N.ShouldBe(42);
    }

    [Fact]
    public async Task Forward_IntOutput_BadValue_ThrowsAdapterParseException()
    {
        var chat = new FakeChatClient()
            .EnqueueText("final_answer: not-a-number");
        var react = new ReAct<IntOut>(Ctx(chat), tools: [EchoTool()]);
        await Should.ThrowAsync<AdapterParseException>(() =>
            react.ForwardAsync(new IntOut { Q = "?" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Forward_InstructionsAreIncludedInSystemMessage()
    {
        var chat = new FakeChatClient()
            .EnqueueText("final_answer: hi");
        var react = new ReAct<GreetOut>(Ctx(chat), tools: [EchoTool()]);
        await react.ForwardAsync(new GreetOut { Name = "Alice" }, ct: TestContext.Current.CancellationToken);
        var systemMsg = chat.RecordedCalls[0].Messages.First(m => m.Role == ChatRole.System);
        (systemMsg.Text ?? "").ShouldContain("Produce a greeting.");
    }

    [Fact]
    public async Task Forward_TraceContainsReasoningMetadata()
    {
        var chat = new FakeChatClient()
            .EnqueueText("Thought: simple.\nfinal_answer: yes");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [EchoTool()]);
        await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        sink.Records[0].Metadata.ShouldContainKey(TraceKeys.Reasoning);
        var reasoning = sink.Records[0].Metadata[TraceKeys.Reasoning]?.ToString() ?? "";
        reasoning.ShouldContain("Thought: simple.");
    }

    [Fact]
    public async Task Forward_FinalAnswerWithNoMarker_ThrowsAdapterParseException()
    {
        // Direct path (not forced): text with no final_answer: marker and no tool calls
        // should throw AdapterParseException.
        var chat = new FakeChatClient()
            .EnqueueText("I have no idea");
        var react = new ReAct<QA>(Ctx(chat), tools: [EchoTool()]);
        await Should.ThrowAsync<AdapterParseException>(() =>
            react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Forward_JsonOutput_Success()
    {
        // Covers the JSON deserialization branch (non-primitive, non-string output).
        var chat = new FakeChatClient()
            .EnqueueText("final_answer: [1,2,3]");
        var react = new ReAct<IntArrayOut>(Ctx(chat), tools: [EchoTool()]);
        var pred = await react.ForwardAsync(new IntArrayOut { Q = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Items.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Forward_JsonOutput_BadValue_ThrowsAdapterParseException()
    {
        // Covers the JSON deserialization error arm.
        var chat = new FakeChatClient()
            .EnqueueText("final_answer: not-valid-json-array");
        var react = new ReAct<IntArrayOut>(Ctx(chat), tools: [EchoTool()]);
        await Should.ThrowAsync<AdapterParseException>(() =>
            react.ForwardAsync(new IntArrayOut { Q = "?" }, ct: TestContext.Current.CancellationToken));
    }

    // â”€â”€ Branch-closure tests for async state machine coverage â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Forward_NullToolArguments_DispatchesToolWithEmptyDict()
    {
        // FunctionCallContent with Arguments=null exercises the
        // `call.Arguments ?? new Dictionary<string, object?>()` branches
        // (ReAct.cs lines 138 and 142).
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "echo", arguments: null));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueText("final_answer: echoed:");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [EchoTool()]);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("echoed:");
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].FunctionName.ShouldBe("echo");
    }

    [Fact]
    public async Task Forward_NullToolArguments_ToolThrows_RecordsFailed()
    {
        // FunctionCallContent with Arguments=null AND a throwing tool exercises
        // the `call.Arguments ?? new Dictionary<string, object?>()` branch
        // inside the catch arm (ReAct.cs line 155).
        var throwingTool = AIFunctionFactory.Create(
            (string input) => ThrowAndReturn(),
            "thrower", "A tool that always throws.");
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "thrower", arguments: null));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueText("final_answer: recovered");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [throwingTool]);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("recovered");
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls[0].Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Forward_ToolThrow_WithActivityListener_TagsChildActivityFailed()
    {
        // Tool throws + ActivityListener active â†’ childActivity is non-null â†’
        // covers childActivity?.SetTag("tool.success", false) branch (ReAct.cs line 160).
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var throwingTool = AIFunctionFactory.Create(
            (string input) => ThrowAndReturn(),
            "thrower", "A tool that always throws.");
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "thrower",
            new Dictionary<string, object?> { ["input"] = "x" }));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueText("final_answer: recovered");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [throwingTool]);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("recovered");
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls[0].Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Forward_Error_WithActivityListener_TagsExceptionOnActivity()
    {
        // Error path (catch block) with ActivityListener installed â†’ activity is non-null â†’
        // covers activity?.SetStatus, activity?.SetTag("exception.type/message") branches
        // (ReAct.cs lines 244â€“246).
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        string? capturedExType = null;
        listener.ActivityStopped = a =>
        {
            if (a.TraceId != parentTraceId) return;
            var v = a.GetTagItem("exception.type")?.ToString();
            if (v is not null) capturedExType = v;
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var chat = new FakeChatClient().EnqueueText("no marker here");
        var react = new ReAct<QA>(Ctx(chat), tools: [EchoTool()]);
        await Should.ThrowAsync<AdapterParseException>(() =>
            react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));

        capturedExType.ShouldNotBeNull();
        capturedExType.ShouldContain(nameof(AdapterParseException));
    }

    [Fact]
    public async Task Forward_LmThrowsAfterToolResult_ErrorPathHasToolRoleLastMessage()
    {
        // Arranges a scenario where the LM call throws AFTER a successful tool result
        // has been appended to messages. When the outer catch fires, messages[^1] is
        // the Tool-role message (which has FunctionResultContent, no text content).
        // This exercises the messages[^1].Text ?? "" null branch in the error TraceRecord
        // (ReAct.cs line 232/239).
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "echo",
            new Dictionary<string, object?> { ["input"] = "x" }));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))                            // iteration 0: tool call
            .EnqueueError(new InvalidOperationException("lm-failure"));       // iteration 1: LM throws
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [EchoTool()]);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));
        // Verify the error trace was written with the ToolCalls metadata intact.
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<InvalidOperationException>();
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].FunctionName.ShouldBe("echo");
    }

    [Fact]
    public async Task Forward_NoArgTool_NullArguments_SuccessRecord_CoversLine142()
    {
        // A tool that takes no parameters + FunctionCallContent with null arguments.
        // When the tool call succeeds, ToolCallRecord.Success receives
        // `call.Arguments ?? new Dictionary<string, object?>()` â€” the null side of line 142.
        var noArgTool = AIFunctionFactory.Create(() => "noarg-result", "noarg", "A no-arg tool.");
        var toolMsg = new ChatMessage(ChatRole.Assistant, "");
        toolMsg.Contents.Add(new FunctionCallContent("c1", "noarg", arguments: null));
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(toolMsg))
            .EnqueueText("final_answer: noarg-result");
        var sink = new InMemoryTraceSink();
        var react = new ReAct<QA>(Ctx(chat, sink), tools: [noArgTool]);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("noarg-result");
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].FunctionName.ShouldBe("noarg");
        toolCalls[0].Result!.ToString().ShouldBe("noarg-result");
    }

    /// <summary>
    /// A <see cref="ITraceSink"/> that throws <see cref="InvalidOperationException"/>
    /// on the FIRST <see cref="Write"/> call and succeeds on all subsequent calls.
    /// Used to force the outer catch of <c>ReAct.ForwardAsync</c> to fire with
    /// <c>outputs</c> non-null (covers the non-null branch of
    /// <c>outputs ?? new Dictionary&lt;string, object?&gt;()</c> on ReAct.cs line 237).
    /// </summary>
    sealed class ThrowOnceTraceSink : ITraceSink
    {
        int _count;
        public List<TraceRecord> Records { get; } = [];
        public void Write(TraceRecord record)
        {
            if (_count++ == 0) throw new InvalidOperationException("sink-boom");
            Records.Add(record);
        }
    }

    [Fact]
    public async Task Forward_TraceSinkThrowsOnSuccessWrite_CatchHasOutputsNonNull()
    {
        // When the TraceSink.Write throws during the SUCCESS path (after outputs is set),
        // the outer catch fires with outputs != null.
        // This covers the non-null branch of `outputs ?? new Dictionary<string, object?>()`
        // (ReAct.cs line 237) and also exercises the error-path TraceRecord write.
        var throwOnceSink = new ThrowOnceTraceSink();
        var chat = new FakeChatClient().EnqueueText("final_answer: result");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), throwOnceSink, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var react = new ReAct<QA>(ctx, tools: [EchoTool()]);
        // The sink throws on the success write; ReAct catches it, writes an error trace
        // (second Write, which succeeds), then re-throws the sink exception.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken));
        // The error trace (second Write) should have been recorded.
        throwOnceSink.Records.Count.ShouldBe(1);
        throwOnceSink.Records[0].Error.ShouldBeOfType<InvalidOperationException>();
    }

}

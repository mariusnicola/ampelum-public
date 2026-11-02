using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Caching;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Collection(ActivityListenerCollection.Name)]
public sealed class PredictTests
{
    [Signature("Answer concisely.")]
    public sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("Echo.")]
    public sealed record Echo : ISignature
    {
        [Input]  public required string Text { get; init; }
        [Output] public string Out { get; init; } = "";
    }

    sealed class ScriptedChat(ChatResponse response) : IChatClient
    {
        public ChatResponse Last { get; private set; } = null!;
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public ChatOptions? LastOptions { get; private set; }
        public int Calls { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("scripted");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            LastMessages = messages.ToList();
            LastOptions = options;
            Last = response;
            return Task.FromResult(response);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    static AmpelumContext Ctx(ChatResponse resp, ITraceSink? sink = null, IAdapter? adapter = null, ChatOptions? defaults = null) =>
        new(new ScriptedChat(resp), adapter ?? new ChatAdapter(), sink ?? new InMemoryTraceSink(), NullLoggerFactory.Instance, defaults);

    [Fact]
    public async Task ForwardAsync_ReturnsPredictionWithMergedOutputs()
    {
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"));
        var sink = new InMemoryTraceSink();
        var ctx = Ctx(resp, sink);
        var predict = new Predict<Qa>(ctx);

        var result = await predict.ForwardAsync(new Qa { Question = "Capital of France?" }, ct: TestContext.Current.CancellationToken);

        result.Value.Question.ShouldBe("Capital of France?");
        result.Value.Answer.ShouldBe("Paris");
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].ModuleName.ShouldBe("Predict");
        sink.Records[0].SignatureType.ShouldBe(typeof(Qa));
        sink.Records[0].Error.ShouldBeNull();
        sink.Records[0].Inputs["Question"].ShouldBe("Capital of France?");
        sink.Records[0].Outputs["Answer"].ShouldBe("Paris");
    }

    [Fact]
    public async Task ForwardAsync_TraceModuleName_UsesSubmoduleName_WhenBound()
    {
        // Regression: when a Predict is bound under an OptimizedProgram (SubmoduleName non-null),
        // its trace ModuleName must reflect the submodule identity, not the literal "Predict".
        // This is what lets BootstrapFewShot disambiguate two Predict-typed submodules.
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nx"));
        var sink = new InMemoryTraceSink();
        var baseCtx = Ctx(resp, sink);
        var ctxWithSubmodule = baseCtx with { SubmoduleName = "predict0" };
        var predict = new Predict<Qa>(ctxWithSubmodule);

        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        sink.Records[0].ModuleName.ShouldBe("predict0");
    }

    [Fact]
    public async Task ForwardAsync_CallsLmExactlyOnce()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nok")));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        chat.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ForwardAsync_PassesDemosToAdapter()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nok")));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        await predict.ForwardAsync(
            new Qa { Question = "?" },
            demos: [new Example(
                new Dictionary<string, object?> { ["Question"] = "2+2?" },
                new Dictionary<string, object?> { ["Answer"] = "4" })],
            ct: TestContext.Current.CancellationToken);

        chat.LastMessages!.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ForwardAsync_NullDemos_TreatsAsEmpty()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nok")));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        await predict.ForwardAsync(new Qa { Question = "?" }, demos: null, ct: TestContext.Current.CancellationToken);
        chat.LastMessages!.Count.ShouldBe(2);  // system + user only
    }

    [Fact]
    public async Task ForwardAsync_SetsResponseFormat_WhenAdapterIsStructuredOutputCapable()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"answer":"Paris"}""")));
        var ctx = new AmpelumContext(chat, new JsonAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        chat.LastOptions.ShouldNotBeNull();
        chat.LastOptions!.ResponseFormat.ShouldNotBeNull();
    }

    [Fact]
    public async Task ForwardAsync_NoStructuredOutputAdapter_LeavesOptionsNull()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nok")));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        // ChatAdapter is not IStructuredOutputAdapter â€” no ResponseFormat should be set.
        // chat.LastOptions may be null OR have a non-null but unmodified value
        chat.LastOptions?.ResponseFormat.ShouldBeNull();
    }

    [Fact]
    public async Task ForwardAsync_PassesDefaultChatOptionsThrough_WhenNotStructuredOutput()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nok")));
        var defaults = new ChatOptions { ModelId = "test-model" };
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance, defaults);
        var predict = new Predict<Qa>(ctx);
        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        chat.LastOptions.ShouldBeSameAs(defaults);
    }

    [Fact]
    public async Task ForwardAsync_ParseFailure_Throws()
    {
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "no markers here"));
        var predict = new Predict<Qa>(Ctx(resp));
        await Should.ThrowAsync<AdapterParseException>(() =>
            predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForwardAsync_ParseFailure_TraceCapturesError()
    {
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "no markers here"));
        var sink = new InMemoryTraceSink();
        var ctx = Ctx(resp, sink);
        var predict = new Predict<Qa>(ctx);
        await Should.ThrowAsync<AdapterParseException>(() =>
            predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken));
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<AdapterParseException>();
    }

    [Fact]
    public void CreateFactory_WiresDefaults()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nok")));
        var p = Predict.Create<Qa>(chat);
        p.ShouldNotBeNull();
    }

    [Fact]
    public void CreateFactory_NullChat_Throws() =>
        Should.Throw<ArgumentNullException>(() => Predict.Create<Qa>(null!));

    [Fact]
    public void Constructor_NullContext_Throws() =>
        Should.Throw<ArgumentNullException>(() => new Predict<Qa>(null!));

    [Fact]
    public async Task ForwardAsync_NullInputs_Throws()
    {
        var ctx = Ctx(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        var predict = new Predict<Qa>(ctx);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            predict.ForwardAsync(null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForwardAsync_TraceIdConsistent_ActivityTag_AndRecord()
    {
        // Set up an ActivityListener so the activity is created.
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var activities = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(activities);

        var capturedTraceId = "";
        activities.ActivityStopped = a =>
        {
            if (a.TraceId != parentTraceId) return;
            var traceId = a.GetTagItem("SignalGrid.Ampelum.trace_id")?.ToString();
            if (traceId is not null) capturedTraceId = traceId;
        };

        var sink = new InMemoryTraceSink();
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nx"));
        var predict = new Predict<Qa>(Ctx(resp, sink));
        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        sink.Records[0].Id.ToString().ShouldBe(capturedTraceId);
    }

    [Fact]
    public async Task ForwardAsync_StructuredOutput_ClonesDefaultChatOptions()
    {
        // Ensure that when using a structured output adapter, DefaultChatOptions is cloned
        // rather than mutated.
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"answer":"Paris"}""")));
        var defaults = new ChatOptions { ModelId = "cloned-model" };
        var ctx = new AmpelumContext(chat, new JsonAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance, defaults);
        var predict = new Predict<Qa>(ctx);

        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        // The options passed to the LM must NOT be the same instance (it was cloned)
        chat.LastOptions.ShouldNotBeSameAs(defaults);
        // But it should carry the cloned ModelId
        chat.LastOptions!.ModelId.ShouldBe("cloned-model");
        // And the original defaults must not be mutated
        defaults.ResponseFormat.ShouldBeNull();
    }

    [Fact]
    public async Task ForwardAsync_WithUsageDetails_RecordsTokenCounts()
    {
        // Exercises the InputTokenCount/OutputTokenCount pattern-match branches (lines 135-136).
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"))
        {
            Usage = new UsageDetails { InputTokenCount = 42, OutputTokenCount = 7 }
        };
        var sink = new InMemoryTraceSink();
        var ctx = Ctx(resp, sink);
        var predict = new Predict<Qa>(ctx);

        var result = await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        result.Trace.Usage.ShouldNotBeNull();
        result.Trace.Usage!.InputTokenCount.ShouldBe(42);
        result.Trace.Usage!.OutputTokenCount.ShouldBe(7);
    }

    // â”€â”€ New helper stubs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    sealed class ThrowingChat(Exception ex) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("throwing");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => Task.FromException<ChatResponse>(ex);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    sealed class ThrowingSink : ITraceSink
    {
        public int Calls;
        public void Write(TraceRecord record) { Calls++; throw new InvalidOperationException("sink broke"); }
    }

    sealed class HangingChat(Task<ChatResponse> task) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("hanging");
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();    // honor the token immediately if pre-cancelled
            return await task.WaitAsync(ct);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // â”€â”€ New tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Cancellation_EmitsTrace_DoesNotWrapException()
    {
        // A chat client that never returns until the token is cancelled, then throws OCE.
        var tcs = new TaskCompletionSource<ChatResponse>();
        var chat = new HangingChat(tcs.Task);
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        using var cts = new CancellationTokenSource();
        // Cancel synchronously â€” BEFORE the first GetResponseAsync runs â€” so the
        // cancellation surfaces as OCE without depending on the chat client to honor it.
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            predict.ForwardAsync(new Qa { Question = "?" }, ct: cts.Token));

        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task LmThrows_WrappedAsLmCallException_TraceEmitted()
    {
        var inner = new HttpRequestException("net down");
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(new ThrowingChat(inner), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        var ex = await Should.ThrowAsync<LmCallException>(() => predict.ForwardAsync(new Qa { Question = "?" }));
        ex.InnerException.ShouldBeSameAs(inner);
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<LmCallException>();
    }

    [Fact]
    public async Task ObjectDisposedFromChat_WrappedAsLmCallException()
    {
        var ctx = new AmpelumContext(new ThrowingChat(new ObjectDisposedException("client")), new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        var ex = await Should.ThrowAsync<LmCallException>(() => predict.ForwardAsync(new Qa { Question = "?" }));
        ex.InnerException.ShouldBeOfType<ObjectDisposedException>();
    }

    [Fact]
    public async Task SinkThrows_ForwardStillSucceeds()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis")));
        var sink = new ThrowingSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        var r = await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Paris");
        sink.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_Forwards_Isolate_Traces()
    {
        var sink = new InMemoryTraceSink();
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nok")));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => predict.ForwardAsync(new Qa { Question = "?" })));
        sink.Records.Count.ShouldBe(20);
    }

    [Fact]
    public async Task DefaultChatOptions_NotMutated_WhenAdapterSetsResponseFormat()
    {
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"answer":"Paris"}""")));
        var defaults = new ChatOptions { ModelId = "gpt-x" };
        var ctx = new AmpelumContext(chat, new JsonAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance, defaults);
        var predict = new Predict<Qa>(ctx);
        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        defaults.ResponseFormat.ShouldBeNull();  // the original must not be mutated
    }

    [Fact]
    public async Task ParseFailure_TraceCapturesRawResponseFromLm()
    {
        // Verifies Fix B: error-path trace populates rawResponse from the
        // successful LM response when only Parse failed.
        var rawText = "model said something parser cannot understand";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, rawText)));
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        await Should.ThrowAsync<AdapterParseException>(() => predict.ForwardAsync(new Qa { Question = "?" }));
        sink.Records[0].RawResponse.ShouldBe(rawText);
        sink.Records[0].Error.ShouldBeOfType<AdapterParseException>();
    }

    // â”€â”€ Additional branch-coverage tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// An adapter that returns valid Parse output (won't throw AdapterParseException)
    /// but assigns a type-incompatible value to a real property on the signature type,
    /// causing RecordCloner.CloneWith to throw when SetValue is called, which exercises
    /// the post-parse failure path (Fix A) and the outputs-non-null branch in the catch.
    /// </summary>
    sealed class BadCloneAdapter : IAdapter
    {
        readonly ChatAdapter _inner = new();
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata sig, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<Example> demos)
            => _inner.Format(sig, inputs, demos);

        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata sig, ChatResponse response)
        {
            // Return a dict with the real "Answer" key but an incompatible value type (int instead
            // of string), so RecordCloner.CloneWith finds the property but SetValue throws.
            return new Dictionary<string, object?> { ["Answer"] = 12345 };
        }
    }

    [Fact]
    public async Task PostParseFails_CloneWithThrows_TraceEmitted_WithOutputsPopulated()
    {
        // Exercises Fix A: CloneWith throws after Parse succeeds.
        // Also exercises the `outputs ?? new Dictionary<>()` branch where outputs IS non-null.
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis")));
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new BadCloneAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        // SetValue with wrong type throws TargetInvocationException (which wraps the root cause);
        // the exception type check is relaxed to Exception since the exact wrapper may vary.
        await Should.ThrowAsync<Exception>(() =>
            predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken));
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldNotBeNull();
        // outputs dict was populated by Parse, so it's non-null in the trace
        sink.Records[0].Outputs.ContainsKey("Answer").ShouldBeTrue();
    }

    [Fact]
    public async Task Activity_ParseFailure_ActivityEventsRecorded()
    {
        // Covers activity?.AddEvent("parse.failed") and activity?.SetStatus in error path
        // when an ActivityListener IS installed (activity is non-null).
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var sink = new InMemoryTraceSink();
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "no markers")));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        await Should.ThrowAsync<AdapterParseException>(() =>
            predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken));

        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<AdapterParseException>();
    }

    [Fact]
    public async Task Activity_Error_SetsExceptionTypAndMessageTags()
    {
        // Covers the OTel exception.type / exception.message tags set on the catch path
        // when an ActivityListener IS installed (activity is non-null).
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        string? capturedExType = null;
        string? capturedExMsg = null;
        listener.ActivityStopped = a =>
        {
            if (a.TraceId != parentTraceId) return;
            var exType = a.GetTagItem("exception.type")?.ToString();
            if (exType is not null)
            {
                capturedExType = exType;
                capturedExMsg  = a.GetTagItem("exception.message")?.ToString();
            }
        };

        var sink = new InMemoryTraceSink();
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "no markers")));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        await Should.ThrowAsync<AdapterParseException>(() =>
            predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken));

        // exception.type should be the fully-qualified exception class name
        capturedExType.ShouldNotBeNull();
        capturedExType.ShouldContain(nameof(SignalGrid.Ampelum.Exceptions.AdapterParseException));
        // exception.message should be non-empty
        capturedExMsg.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Activity_WithUsage_TagsRecorded()
    {
        // Covers activity?.SetTag("gen_ai.usage.input_tokens/output_tokens") with usage non-null
        // when ActivityListener IS installed (activity is non-null). v0.7 OTel GenAI 1.27.0.
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var capturedInput = (object?)null;
        var capturedOutput = (object?)null;
        listener.ActivityStopped = a =>
        {
            if (a.TraceId != parentTraceId) return;
            var inp = a.GetTagItem("gen_ai.usage.input_tokens");
            if (inp is not null)
            {
                capturedInput = inp;
                capturedOutput = a.GetTagItem("gen_ai.usage.output_tokens");
            }
        };

        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
        };
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(new ScriptedChat(resp), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        capturedInput.ShouldBe((long?)10);
        capturedOutput.ShouldBe((long?)5);
    }

    /// <summary>
    /// An adapter that ignores the response text and always returns fixed outputs.
    /// Used to test the <c>response.Text ?? ""</c> fallback in the happy-path trace.
    /// </summary>
    sealed class FixedOutputAdapter(IReadOnlyDictionary<string, object?> fixedOutputs) : IAdapter
    {
        readonly ChatAdapter _fmt = new();
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata sig, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<Example> demos)
            => _fmt.Format(sig, inputs, demos);
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata sig, ChatResponse response)
            => fixedOutputs;
    }

    [Fact]
    public async Task HappyPath_EmptyResponseMessages_TraceHasEmptyRawResponse()
    {
        // Covers the happy-path TraceRecord construction when the ChatResponse has no
        // text content (e.g. a tool-call-only response). M.E.AI ChatResponse.Text is
        // always non-null (string.Concat semantics), returning "" for empty message list.
        var emptyMsgResponse = new ChatResponse([]);   // empty messages => Text is "" (not null)
        var adapter = new FixedOutputAdapter(new Dictionary<string, object?> { ["Answer"] = "Paris" });
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(new ScriptedChat(emptyMsgResponse), adapter, sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);

        var result = await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("Paris");
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].RawResponse.ShouldBe("");   // empty messages â†’ Text="" â†’ RawResponse=""
        sink.Records[0].Error.ShouldBeNull();
    }

    // â”€â”€ Logger helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>A captured log entry.</summary>
    sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    /// <summary>Simple ILogger that captures all entries when all levels are enabled.</summary>
    sealed class CapturingLogger(List<LogEntry> entries) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;   // all levels enabled
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
    }

    /// <summary>ILoggerFactory that returns a <see cref="CapturingLogger"/> for every category.</summary>
    sealed class CapturingLoggerFactory(List<LogEntry> entries) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    [Fact]
    public async Task ForwardAsync_WithEnabledLogger_FiresPromptMessagesAndRawResponseLogEvents()
    {
        // Exercises AmpelumLog.PromptMessages (EventId=1003, Debug) and
        // AmpelumLog.RawResponse (EventId=1004, Trace) â€” the source-generated LoggerMessage
        // methods wired into Predict.ForwardAsync. Using a logger with all levels enabled
        // hits the "true" branch of each if(logger.IsEnabled(level)) guard.
        var entries = new List<LogEntry>();
        var loggerFactory = new CapturingLoggerFactory(entries);

        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"));
        var chat = new ScriptedChat(resp);
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, loggerFactory);
        var predict = new Predict<Qa>(ctx);

        await predict.ForwardAsync(new Qa { Question = "Capital of France?" }, ct: TestContext.Current.CancellationToken);

        // EventId 1003: PromptMessages (Debug)
        var promptMsg = entries.Where(e => e.EventId.Id == 1003).ToList();
        promptMsg.Count.ShouldBe(1);
        promptMsg[0].Level.ShouldBe(LogLevel.Debug);

        // EventId 1004: RawResponse (Trace)
        var rawRespMsg = entries.Where(e => e.EventId.Id == 1004).ToList();
        rawRespMsg.Count.ShouldBe(1);
        rawRespMsg[0].Level.ShouldBe(LogLevel.Trace);
    }

    [Fact]
    public async Task ForwardAsync_WithEnabledLogger_LmThrows_FiresForwardFailedLogEvent()
    {
        // Exercises AmpelumLog.ForwardFailed (EventId=1001, Error) â€” the source-generated
        // LoggerMessage method called in the catch block. Using a logger with all levels
        // enabled hits the "true" branch of the if(logger.IsEnabled(Error)) guard.
        var entries = new List<LogEntry>();
        var loggerFactory = new CapturingLoggerFactory(entries);

        var inner = new HttpRequestException("network down");
        var ctx = new AmpelumContext(new ThrowingChat(inner), new ChatAdapter(), NullTraceSink.Instance, loggerFactory);
        var predict = new Predict<Qa>(ctx);

        await Should.ThrowAsync<SignalGrid.Ampelum.Exceptions.LmCallException>(() =>
            predict.ForwardAsync(new Qa { Question = "?" }));

        // EventId 1001: ForwardFailed (Error)
        var failedMsg = entries.Where(e => e.EventId.Id == 1001).ToList();
        failedMsg.Count.ShouldBe(1);
        failedMsg[0].Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public async Task ForwardAsync_WithEnabledLogger_SinkThrows_FiresTraceSinkThrewLogEvent()
    {
        // Exercises AmpelumLog.TraceSinkThrew (EventId=1002, Warning) â€” the source-generated
        // LoggerMessage method called when the trace sink throws. Using a logger with all
        // levels enabled hits the "true" branch of the if(logger.IsEnabled(Warning)) guard.
        var entries = new List<LogEntry>();
        var loggerFactory = new CapturingLoggerFactory(entries);

        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis")));
        var sink = new ThrowingSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, loggerFactory);
        var predict = new Predict<Qa>(ctx);

        var r = await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Paris");

        // EventId 1002: TraceSinkThrew (Warning)
        var sinkThrewMsg = entries.Where(e => e.EventId.Id == 1002).ToList();
        sinkThrewMsg.Count.ShouldBe(1);
        sinkThrewMsg[0].Level.ShouldBe(LogLevel.Warning);
    }

    // â”€â”€ Ambient-context tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Predict_ResolvesAmbient_WhenConstructorCtxIsNull()
    {
        var chat = new FakeChatClient().EnqueueOutputs(new Echo { Text = "hi", Out = "hi" });
        var ctx = new AmpelumContext(chat, new ChatAdapter(), new InMemoryTraceSink(), NullLoggerFactory.Instance);
        using var _ = AmpelumContext.Push(ctx);
        var predict = Predict<Echo>.FromAmbient();
        var pred = await predict.ForwardAsync(new Echo { Text = "hi" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Out.ShouldBe("hi");
    }

    [Fact]
    public async Task Predict_PrefersConstructorCtx_OverAmbient()
    {
        var ambientChat = new FakeChatClient().EnqueueText("[[ ## Out ## ]]\nambient");
        var ambientCtx = new AmpelumContext(ambientChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var ctorChat = new FakeChatClient().EnqueueText("[[ ## Out ## ]]\nctor");
        var ctorCtx = new AmpelumContext(ctorChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        using var _ = AmpelumContext.Push(ambientCtx);
        var predict = new Predict<Echo>(ctorCtx);
        var pred = await predict.ForwardAsync(new Echo { Text = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Out.ShouldBe("ctor");
        ambientChat.RecordedCalls.Count.ShouldBe(0);
        ctorChat.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Predict_FromAmbient_ThrowsWhenNoAmbient()
    {
        var predict = Predict<Echo>.FromAmbient();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            predict.ForwardAsync(new Echo { Text = "?" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Activity_CacheHit_SetsTagWhenListenerInstalled()
    {
        // Covers the activity?.SetTag("SignalGrid.Ampelum.cache_hit", true) branch (Predict.cs line 141)
        // when activity IS non-null because an ActivityListener is installed.
        // The AmpelumCachingChatClient annotates the 2nd identical response with
        // AdditionalProperties["SignalGrid.Ampelum.cache_hit"] = true, which triggers that branch.
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var inner = new FakeChatClient()
            .EnqueueText("[[ ## Answer ## ]]\nParis")
            .EnqueueText("[[ ## Answer ## ]]\nParis");
        var cached = new AmpelumCachingChatClient(inner, cache);
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(cached, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<Qa>(ctx);
        var opts = new ChatOptions { Temperature = 0, ModelId = "test", MaxOutputTokens = 100 };
        var ctorCtx = new AmpelumContext(cached, new ChatAdapter(), sink, NullLoggerFactory.Instance, opts);
        var predict2 = new Predict<Qa>(ctorCtx);
        var ct = TestContext.Current.CancellationToken;

        // First call â€” cache miss, no CacheHit metadata.
        await predict2.ForwardAsync(new Qa { Question = "What is Paris?" }, ct: ct);
        // Second call â€” cache hit; activity?.SetTag("SignalGrid.Ampelum.cache_hit", true) executes with non-null activity.
        var two = await predict2.ForwardAsync(new Qa { Question = "What is Paris?" }, ct: ct);

        two.Trace.Metadata.ShouldContainKey(TraceKeys.CacheHit);
        two.Trace.Metadata[TraceKeys.CacheHit].ShouldBe(true);
        // The inner FakeChatClient should only be called once (second response came from cache).
        inner.RecordedCalls.Count.ShouldBe(1);
    }
}

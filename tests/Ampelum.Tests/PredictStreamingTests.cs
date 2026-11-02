using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Collection(ActivityListenerCollection.Name)]
public sealed class PredictStreamingTests
{
    [Signature("")] public sealed record S : ISignature
    {
        [Input]  public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    static AmpelumContext Ctx(IChatClient chat, ITraceSink? sink = null, IAdapter? adapter = null) =>
        new(chat, adapter ?? new ChatAdapter(), sink ?? NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task StreamAsync_EmitsPartialUpdates_ThenFinal()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "Paris" });
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(chat, sink));
        var updates = new List<PredictionUpdate<S>>();
        await foreach (var u in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken))
            updates.Add(u);

        updates.Count.ShouldBeGreaterThan(1);
        updates[^1].IsFinal.ShouldBeTrue();
        updates[^1].Value!.A.ShouldBe("Paris");
        updates.Take(updates.Count - 1).ShouldAllBe(u => u.IsFinal == false && u.Value == null);
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeNull();
    }

    [Fact]
    public async Task StreamAsync_NonStreamingAdapter_Throws()
    {
        var chat = new FakeChatClient();
        var predict = new Predict<S>(Ctx(chat, adapter: new NonStreamingAdapter()));
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(new S { Q = "?" })) { }
        });
    }

    [Fact]
    public async Task StreamAsync_ParseFailure_EmitsTraceWithFlag_RethrowsFromAwaitForeach()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("garbage no markers");
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(chat, sink));
        await Should.ThrowAsync<AdapterParseException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(new S { Q = "?" })) { }
        });
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Metadata[TraceKeys.StreamingParseFailure].ShouldBe(true);
        sink.Records[0].Error.ShouldBeOfType<AdapterParseException>();
        sink.Records[0].RawResponse.ShouldBe("garbage no markers");
    }

    [Fact]
    public async Task StreamAsync_Cancellation_EmitsTraceWithOCE()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks("a", "b", "c");
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(chat, sink));
        using var cts = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: cts.Token))
                cts.Cancel();
        });
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task StreamAsync_NullInputs_Throws()
    {
        var chat = new FakeChatClient();
        var predict = new Predict<S>(Ctx(chat));
        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(null!)) { }
        });
    }

    [Fact]
    public async Task StreamAsync_ResolvesAmbient_WhenConstructorCtxIsNull()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "hi" });
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        using var _ = AmpelumContext.Push(ctx);
        var predict = Predict<S>.FromAmbient();
        var updates = new List<PredictionUpdate<S>>();
        await foreach (var u in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken))
            updates.Add(u);
        updates[^1].IsFinal.ShouldBeTrue();
        updates[^1].Value!.A.ShouldBe("hi");
    }

    [Fact]
    public async Task StreamAsync_FromAmbient_ThrowsWhenNoAmbient()
    {
        var predict = Predict<S>.FromAmbient();
        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(new S { Q = "?" })) { }
        });
    }

    [Fact]
    public async Task StreamAsync_Activity_Success_SetsTraceIdTag()
    {
        // Exercises activity?.SetTag("SignalGrid.Ampelum.trace_id") on the success path when an
        // ActivityListener IS installed (activity is non-null).
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        string? capturedTraceId = null;
        listener.ActivityStopped = a =>
        {
            if (a.TraceId != parentTraceId) return;
            // Only capture when SignalGrid.Ampelum.trace_id is present to avoid overwriting with null
            // if an unrelated activity fires before the expected activity.
            var traceId = a.GetTagItem("SignalGrid.Ampelum.trace_id")?.ToString();
            if (traceId is not null)
                capturedTraceId = traceId;
        };

        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "ok" });
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(chat, sink));
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken)) { }

        capturedTraceId.ShouldNotBeNullOrEmpty();
        sink.Records[0].Id.ToString().ShouldBe(capturedTraceId);
    }

    [Fact]
    public async Task StreamAsync_Activity_Error_SetsExceptionTags()
    {
        // Exercises activity?.SetStatus, activity?.SetTag("exception.type"), activity?.SetTag("exception.message")
        // on the error path when an ActivityListener IS installed (activity is non-null).
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        string? capturedExType = null;
        string? capturedExMsg = null;
        listener.ActivityStopped = a =>
        {
            if (a.TraceId != parentTraceId) return;
            // Only capture when exception.type is present to avoid overwriting with null
            // if an unrelated activity fires before the error activity.
            var exType = a.GetTagItem("exception.type")?.ToString();
            if (exType is not null)
            {
                capturedExType = exType;
                capturedExMsg  = a.GetTagItem("exception.message")?.ToString();
            }
        };

        var chat = new FakeChatClient().EnqueueStreamingChunks("garbage no markers");
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(chat, sink));
        await Should.ThrowAsync<AdapterParseException>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(new S { Q = "?" })) { }
        });

        capturedExType.ShouldNotBeNull();
        capturedExType.ShouldContain(nameof(AdapterParseException));
        capturedExMsg.ShouldNotBeNullOrEmpty();
    }

    // C1 â€” PartialText must not contain raw markers
    [Fact]
    public async Task StreamAsync_PartialText_DoesNotContainRawMarkers()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "Paris" });
        var predict = new Predict<S>(Ctx(chat));
        await foreach (var u in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken))
        {
            u.PartialText.ShouldNotContain("[[");
            u.PartialText.ShouldNotContain("## ]]");
        }
    }

    // C2 â€” StreamAsync: cache-hit propagates to Trace.Metadata
    [Fact]
    public async Task StreamAsync_CacheHit_PropagatesToTraceMetadata()
    {
        var cache = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        var inner = new FakeChatClient()
            .EnqueueStreamingOutputs(new S { Q = "?", A = "Paris" })
            .EnqueueStreamingOutputs(new S { Q = "?", A = "Paris" });
        var caching = new SignalGrid.Ampelum.Caching.AmpelumCachingChatClient(inner, cache);
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(caching, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var predict = new Predict<S>(ctx);
        var ct = TestContext.Current.CancellationToken;

        // First call â€” cache miss, populates cache
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: ct)) { }
        sink.Clear();

        // Second call â€” cache hit
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: ct)) { }
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Metadata.ShouldContainKey(TraceKeys.CacheHit);
        sink.Records[0].Metadata[TraceKeys.CacheHit].ShouldBe(true);
    }

    // C2 â€” StreamAsync: cache-hit with ActivityListener active covers activity?.SetTag("SignalGrid.Ampelum.cache_hit", true) branch
    [Fact]
    public async Task StreamAsync_CacheHit_WithActivityListener_SetsActivityTag()
    {
        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new System.Diagnostics.Activity("listener-test").Start();
        var parentTraceId = parent.TraceId;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        bool cacheHitTagSet = false;
        listener.ActivityStopped = a =>
        {
            if (a.TraceId != parentTraceId) return;
            if (a.GetTagItem("SignalGrid.Ampelum.cache_hit") is true)
                cacheHitTagSet = true;
        };

        var cache = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        var inner = new FakeChatClient()
            .EnqueueStreamingOutputs(new S { Q = "?", A = "Lyon" })
            .EnqueueStreamingOutputs(new S { Q = "?", A = "Lyon" });
        var caching = new SignalGrid.Ampelum.Caching.AmpelumCachingChatClient(inner, cache);
        var ctx = new AmpelumContext(caching, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var predict = new Predict<S>(ctx);
        var ct = TestContext.Current.CancellationToken;

        // First call â€” cache miss
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: ct)) { }
        // Second call â€” cache hit; activity?.SetTag("SignalGrid.Ampelum.cache_hit", true) branch exercised
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: ct)) { }

        cacheHitTagSet.ShouldBeTrue();
    }

    // C2 â€” StreamAsync: no cache-hit when it's a miss
    [Fact]
    public async Task StreamAsync_NoCacheHit_TraceMetadataDoesNotContainCacheHitKey()
    {
        var inner = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "Paris" });
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(inner, sink));
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken)) { }
        sink.Records[0].Metadata.ContainsKey(TraceKeys.CacheHit).ShouldBeFalse();
    }

    // I3 â€” StreamAsync: Usage from UsageContent in final chunk propagates to Trace
    [Fact]
    public async Task StreamAsync_UsageInFinalChunk_PropagatesUsageToTrace()
    {
        var usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 };
        var chat = new UsageStreamingChatClient(new S { Q = "?", A = "Rome" }, usage);
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(chat, sink));
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken)) { }
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Usage.ShouldNotBeNull();
        sink.Records[0].Usage!.InputTokenCount.ShouldBe(7);
        sink.Records[0].Usage!.OutputTokenCount.ShouldBe(3);
    }

    // I3 â€” StreamAsync: no usage when chunks carry no UsageContent
    [Fact]
    public async Task StreamAsync_NoUsageInChunks_TraceUsageIsNull()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "Berlin" });
        var sink = new InMemoryTraceSink();
        var predict = new Predict<S>(Ctx(chat, sink));
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken)) { }
        sink.Records[0].Usage.ShouldBeNull();
    }

    [Fact]
    public async Task StreamAsync_HookPipeline_OnBefore_OnAfter_FireOnce()
    {
        var chat = new FakeChatClient().EnqueueStreamingOutputs(new S { Q = "?", A = "Hi" });
        var hook = new RecordingHook();
        var ctx = new AmpelumContext(chat, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance)
        { Hooks = new SignalGrid.Ampelum.Hooks.IHook[] { hook } };
        var predict = new Predict<S>(ctx);
        await foreach (var _ in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken)) { }
        hook.Before.ShouldBe(1);
        hook.After.ShouldBe(1);
        hook.Errors.ShouldBe(0);
        hook.LastOutputs.ShouldNotBeNull();
        hook.LastOutputs!["A"].ShouldBe("Hi");
    }

    [Fact]
    public async Task StreamAsync_OnBeforeShortCircuit_SkipsLmCall_RunsOnAfter()
    {
        // Hook writes Outputs in OnBefore; LM stream should be skipped, OnAfter still fires.
        var chat = new FakeChatClient(); // no enqueued response
        var hook = new ShortCircuitHook(new Dictionary<string, object?> { ["A"] = "from-hook" });
        var ctx = new AmpelumContext(chat, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance)
        { Hooks = new SignalGrid.Ampelum.Hooks.IHook[] { hook } };
        var predict = new Predict<S>(ctx);
        S? final = null;
        await foreach (var u in predict.StreamAsync(new S { Q = "?" }, ct: TestContext.Current.CancellationToken))
        {
            if (u.IsFinal) final = u.Value;
        }
        final.ShouldNotBeNull();
        final!.A.ShouldBe("from-hook");
        hook.AfterFired.ShouldBeTrue();
    }

    [Fact]
    public async Task StreamAsync_OnError_FiresWhenAdapterFinalizeThrows()
    {
        // Empty response â†’ ChatAdapter.Finalize throws AdapterParseException.
        var chat = new FakeChatClient().EnqueueStreamingChunks("garbage no markers");
        var hook = new RecordingHook();
        var ctx = new AmpelumContext(chat, new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance)
        { Hooks = new SignalGrid.Ampelum.Hooks.IHook[] { hook } };
        var predict = new Predict<S>(ctx);
        await Should.ThrowAsync<Exception>(async () =>
        {
            await foreach (var _ in predict.StreamAsync(new S { Q = "?" },
                ct: TestContext.Current.CancellationToken)) { }
        });
        hook.Before.ShouldBe(1);
        hook.Errors.ShouldBe(1);
    }

    sealed class RecordingHook : SignalGrid.Ampelum.Hooks.IHook
    {
        public int Before, After, Errors;
        public IReadOnlyDictionary<string, object?>? LastOutputs;
        public int Order => 0;
        public ValueTask OnBeforeAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        { Before++; return default; }
        public ValueTask OnAfterAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        { After++; LastOutputs = ctx.Outputs; return default; }
        public ValueTask OnErrorAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, Exception error, CancellationToken ct)
        { Errors++; return default; }
    }

    sealed class ShortCircuitHook(IReadOnlyDictionary<string, object?> outputs) : SignalGrid.Ampelum.Hooks.IHook
    {
        public bool AfterFired;
        public int Order => 0;
        public ValueTask OnBeforeAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        { ctx.Outputs = outputs; return default; }
        public ValueTask OnAfterAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, CancellationToken ct)
        { AfterFired = true; return default; }
        public ValueTask OnErrorAsync(SignalGrid.Ampelum.Hooks.HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    sealed class NonStreamingAdapter : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> i, IReadOnlyList<Example> d) => [];
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) => new Dictionary<string, object?>();
    }

    /// <summary>Streaming client that emits normal field chunks then a final chunk with UsageContent.</summary>
    sealed class UsageStreamingChatClient(S outputs, UsageDetails usage) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("UsageStreamingChatClient");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Emit the field chunks via FakeChatClient's adapter logic
            var adapter = new ChatAdapter();
            var meta = SignatureMetadataCache.For<S>();
            var dict = new Dictionary<string, object?> { ["A"] = outputs.A };
            var wire = ((ITestableAdapter)adapter).FormatResponse(meta, dict).Text!;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, wire);
            // Emit final chunk with UsageContent
            await Task.Yield();
            var usageUpdate = new ChatResponseUpdate(ChatRole.Assistant, "");
            usageUpdate.Contents.Add(new UsageContent(usage));
            yield return usageUpdate;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

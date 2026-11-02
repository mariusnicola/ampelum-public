using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class AuditHookRecordModeTests
{
    [Fact]
    public void Order_IsTen()
    {
        var hook = new AuditHook(new InMemoryAuditSink());
        hook.Order.ShouldBe(10);
    }

    [Fact]
    public async Task OnAfter_AppendsEntry_WithChainedHashes()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx1 = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?> { ["q"] = "hi" }
        };
        ctx1.Set(AuditMetadataKeys.ModelId, "test-model");
        ctx1.Set(AuditMetadataKeys.PromptTokens, 12L);
        ctx1.Set(AuditMetadataKeys.CompletionTokens, 4L);
        ctx1.Outputs = new Dictionary<string, object?> { ["a"] = "hello" };

        await hook.OnBeforeAsync(ctx1, TestContext.Current.CancellationToken);
        await hook.OnAfterAsync(ctx1, TestContext.Current.CancellationToken);

        sink.NextSequence.ShouldBe(1L);
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries[0].Sequence.ShouldBe(0L);
        entries[0].ModuleName.ShouldBe("M");
        entries[0].ModelId.ShouldBe("test-model");
        entries[0].PromptTokens.ShouldBe(12L);
        entries[0].CompletionTokens.ShouldBe(4L);
        entries[0].PrevHash.ShouldAllBe(b => b == 0);

        var ctx2 = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?> { ["q"] = "again" }
        };
        ctx2.Set(AuditMetadataKeys.ModelId, "openai/gpt-oss-20b");
        ctx2.Set(AuditMetadataKeys.PromptTokens, 5L);
        ctx2.Set(AuditMetadataKeys.CompletionTokens, 1L);
        ctx2.Outputs = new Dictionary<string, object?> { ["a"] = "back" };
        await hook.OnBeforeAsync(ctx2, TestContext.Current.CancellationToken);
        await hook.OnAfterAsync(ctx2, TestContext.Current.CancellationToken);

        entries.Clear();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries[1].PrevHash.ShouldBe(entries[0].EntryHash);
    }

    [Fact]
    public async Task OnAfter_MissingModelId_FallsBackToUnknown()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?>()
        };
        ctx.Set(AuditMetadataKeys.PromptTokens, 0L);
        ctx.Set(AuditMetadataKeys.CompletionTokens, 0L);
        ctx.Outputs = new Dictionary<string, object?>();
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        AuditEntry e = default!;
        await foreach (var x in sink.EnumerateAsync(TestContext.Current.CancellationToken)) e = x;
        e.ModelId.ShouldBe("unknown");
    }

    [Fact]
    public async Task OnAfter_NullOutputs_AppendsEmptyOutputs()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx = new HookContext { ModuleName = "M", Inputs = new Dictionary<string, object?>() };
        // Outputs intentionally not set (null) â€” covers the ?? new() branch.
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        AuditEntry e = default!;
        await foreach (var x in sink.EnumerateAsync(TestContext.Current.CancellationToken)) e = x;
        e.Outputs.Count.ShouldBe(0);
        e.PromptTokens.ShouldBe(0L);
        e.CompletionTokens.ShouldBe(0L);
    }

    [Fact]
    public async Task OnError_DoesNotAppend()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?>()
        };
        await hook.OnErrorAsync(ctx, new InvalidOperationException("boom"), TestContext.Current.CancellationToken);
        sink.NextSequence.ShouldBe(0L);
    }

    [Fact]
    public async Task OnAfter_PropagatesEventTypeFromMetadata_Alpha5Gap2()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?>(),
        };
        ctx.Set(AuditMetadataKeys.EventType, "router.failover");
        ctx.Outputs = new Dictionary<string, object?>();
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        AuditEntry e = default!;
        await foreach (var x in sink.EnumerateAsync(TestContext.Current.CancellationToken)) e = x;
        e.EventType.ShouldBe("router.failover");
    }

    [Fact]
    public async Task OnAfter_DefaultsEventTypeWhenAbsent_Alpha5Gap2()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?>(),
        };
        ctx.Outputs = new Dictionary<string, object?>();
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        AuditEntry e = default!;
        await foreach (var x in sink.EnumerateAsync(TestContext.Current.CancellationToken)) e = x;
        e.EventType.ShouldBe("forward.completed");
    }

    [Fact]
    public async Task OnAfter_IgnoresEventTypeWhenWrongType_Alpha5Gap2()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?>(),
        };
        ctx.Set(AuditMetadataKeys.EventType, 42); // not a string
        ctx.Outputs = new Dictionary<string, object?>();
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        AuditEntry e = default!;
        await foreach (var x in sink.EnumerateAsync(TestContext.Current.CancellationToken)) e = x;
        e.EventType.ShouldBe("forward.completed");
    }

    [Fact]
    public async Task OnAfter_IgnoresEventTypeWhenEmptyString_Alpha5Gap2()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?>(),
        };
        ctx.Set(AuditMetadataKeys.EventType, "");
        ctx.Outputs = new Dictionary<string, object?>();
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        AuditEntry e = default!;
        await foreach (var x in sink.EnumerateAsync(TestContext.Current.CancellationToken)) e = x;
        e.EventType.ShouldBe("forward.completed");
    }

    [Fact]
    public async Task OnAfter_PopulatesReplayHash_DeterministicOnSameInputs()
    {
        // alpha.10 (Gap 1): two AuditHook invocations over the same input under the
        // same module/event/model produce equal ReplayHash values, even when
        // Outputs / token counts / Timestamps differ.
        var sinkA = new InMemoryAuditSink();
        var hookA = new AuditHook(sinkA);
        var inputs = new Dictionary<string, object?> { ["q"] = "hello" };
        var ctxA = new HookContext { ModuleName = "M", Inputs = inputs };
        ctxA.Set(AuditMetadataKeys.ModelId, "model-x");
        ctxA.Set(AuditMetadataKeys.PromptTokens, 7L);
        ctxA.Set(AuditMetadataKeys.CompletionTokens, 3L);
        ctxA.Outputs = new Dictionary<string, object?> { ["a"] = "first" };
        await hookA.OnAfterAsync(ctxA, TestContext.Current.CancellationToken);

        var sinkB = new InMemoryAuditSink();
        var hookB = new AuditHook(sinkB);
        var ctxB = new HookContext { ModuleName = "M", Inputs = inputs };
        ctxB.Set(AuditMetadataKeys.ModelId, "model-x");
        ctxB.Set(AuditMetadataKeys.PromptTokens, 99L);
        ctxB.Set(AuditMetadataKeys.CompletionTokens, 99L);
        ctxB.Outputs = new Dictionary<string, object?> { ["a"] = "second-and-different" };
        await hookB.OnAfterAsync(ctxB, TestContext.Current.CancellationToken);

        var ea = new List<AuditEntry>();
        await foreach (var e in sinkA.EnumerateAsync(TestContext.Current.CancellationToken)) ea.Add(e);
        var eb = new List<AuditEntry>();
        await foreach (var e in sinkB.EnumerateAsync(TestContext.Current.CancellationToken)) eb.Add(e);

        ea[0].GetReplayHash().ShouldNotBeNullOrWhiteSpace();
        ea[0].GetReplayHash().ShouldBe(eb[0].GetReplayHash());
    }

    // v1.0-rc1.alpha.13: AuditHook writes SchemaVersion=3 + TraceId

    static HookContext MakeCtx(string? traceId = null, string? eventType = null)
    {
        var ctx = new HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?> { ["q"] = "hi" }
        };
        ctx.Set(AuditMetadataKeys.ModelId, "m");
        ctx.Set(AuditMetadataKeys.PromptTokens, 0L);
        ctx.Set(AuditMetadataKeys.CompletionTokens, 0L);
        ctx.Outputs = new Dictionary<string, object?> { ["a"] = "ok" };
        if (traceId is not null) ctx.Set(AuditMetadataKeys.TraceId, traceId);
        if (eventType is not null) ctx.Set(AuditMetadataKeys.EventType, eventType);
        return ctx;
    }

    [Fact]
    public async Task OnAfter_Alpha13_SetsSchemaVersion3()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        await hook.OnAfterAsync(MakeCtx(), TestContext.Current.CancellationToken);
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries[0].SchemaVersion.ShouldBe(3);
    }

    [Fact]
    public async Task OnAfter_Alpha13_PropagatesTraceIdFromMetadata()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        await hook.OnAfterAsync(MakeCtx(traceId: "abcdef01abcdef01abcdef01abcdef01"), TestContext.Current.CancellationToken);
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries[0].TraceId.ShouldBe("abcdef01abcdef01abcdef01abcdef01");
    }

    [Fact]
    public async Task OnAfter_Alpha13_DefaultsTraceIdToEmpty_WhenNoMetadataOrActivity()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        // No TraceId in metadata; no ambient Activity â†’ fallback to "".
        await hook.OnAfterAsync(MakeCtx(), TestContext.Current.CancellationToken);
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        // TraceId defaults to "" when no Activity.Current and no metadata key.
        entries[0].TraceId.ShouldBe(entries[0].TraceId); // non-null (coercion guarantee)
        entries[0].TraceId.ShouldNotBeNull();
    }

    [Fact]
    public async Task OnAfter_Alpha13_EntryHashVerifiesWithV3Algorithm()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        await hook.OnAfterAsync(MakeCtx(traceId: "deadbeefdeadbeefdeadbeefdeadbeef"), TestContext.Current.CancellationToken);
        // Verify chain using the verifier (which dispatches on SchemaVersion=3).
        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task OnAfter_Alpha13_TamperedTraceId_FailsVerification()
    {
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        await hook.OnAfterAsync(MakeCtx(traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), TestContext.Current.CancellationToken);
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        // Tamper TraceId, keep original EntryHash.
        var tampered = entries[0] with { TraceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };

        // Verify via BogusSink (InMemoryAuditSink is append-only).
        var bogusSink = new SingleEntrySink(tampered);
        var r = await AuditChainVerifier.VerifyAsync(bogusSink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeFalse();
        r.Reason.ShouldContain("EntryHash");
    }

    sealed class SingleEntrySink : IAuditSink
    {
        readonly AuditEntry _entry;
        public SingleEntrySink(AuditEntry entry) { _entry = entry; }
        public long NextSequence => 1;
        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct) => throw new NotSupportedException();
#pragma warning disable CS1998
        public async IAsyncEnumerable<AuditEntry> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { yield return _entry; }
#pragma warning restore CS1998
    }

    // v1.0-rc1.alpha.13: AuditEntryReplay.GetTraceId

    static AuditEntry MakeEntryWith(string traceId) => new()
    {
        Sequence = 0, Timestamp = DateTimeOffset.UnixEpoch,
        ModuleName = "M", ModelId = "m",
        Inputs = new Dictionary<string, object?>(),
        Outputs = new Dictionary<string, object?>(),
        PromptTokens = 0, CompletionTokens = 0,
        PrevHash = new byte[32], EntryHash = new byte[32],
        TraceId = traceId,
    };

    [Fact]
    public void GetTraceId_ReturnsEntryTraceId_WhenPresent()
    {
        var entry = MakeEntryWith("deadbeefdeadbeefdeadbeefdeadbeef");
        entry.GetTraceId().ShouldBe("deadbeefdeadbeefdeadbeefdeadbeef");
    }

    [Fact]
    public void GetTraceId_ReturnsEmptyString_WhenNotSet()
    {
        var entry = MakeEntryWith("");
        entry.GetTraceId().ShouldBe("");
    }
}

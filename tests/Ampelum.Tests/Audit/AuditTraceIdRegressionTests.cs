using System.Diagnostics;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

/// <summary>
/// v1.0-rc1.alpha.13: end-to-end regression tests for the TraceId /
/// SchemaVersion=3 migration. Validates the full pipeline from AuditHook
/// through JsonFileAuditSink round-trip and AuditChainVerifier.
/// </summary>
public sealed class AuditTraceIdRegressionTests
{
    static HookContext MakeCtx(string moduleName = "M", Dictionary<string, object?>? metadata = null)
    {
        var ctx = new HookContext
        {
            ModuleName = moduleName,
            Inputs = new Dictionary<string, object?> { ["q"] = "regression" },
        };
        ctx.Set(AuditMetadataKeys.ModelId, "m");
        ctx.Set(AuditMetadataKeys.PromptTokens, 0L);
        ctx.Set(AuditMetadataKeys.CompletionTokens, 0L);
        ctx.Outputs = new Dictionary<string, object?> { ["a"] = "ok" };
        if (metadata is not null)
            foreach (var (k, v) in metadata)
                ctx.Set(k, v);
        return ctx;
    }

    [Fact]
    public async Task E2E_TraceId_FromMetadata_RoundTripsAndVerifies()
    {
        // Arrange: wire AuditHook with an InMemoryAuditSink; inject TraceId via metadata.
        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        const string tid = "cafebabecafebabecafebabecafebabe";

        var ctx = MakeCtx(metadata: new() { [AuditMetadataKeys.TraceId] = tid });
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        // Assert: TraceId is persisted on the entry.
        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries.Count.ShouldBe(1);
        entries[0].SchemaVersion.ShouldBe(3);
        entries[0].TraceId.ShouldBe(tid);

        // Assert: chain verifies via V3 algorithm.
        var result = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task E2E_LegacyV1Json_DeserializesAndVerifies()
    {
        // Simulate a V1 row written before alpha.13: no TraceId, SchemaVersion=1,
        // EventType excluded from hash. Confirm verifier still accepts it.
        var ins  = new Dictionary<string, object?> { ["q"] = "legacy" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        var prev = AuditChainHasher.ZeroHash;
        var ts   = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var v1Entry = new AuditEntry
        {
            // SchemaVersion not set â†’ defaults to 1 (backward compat).
            EventType = "forward.completed",
            Sequence = 0, Timestamp = ts, ModuleName = "Legacy", ModelId = "m",
            Inputs = ins, Outputs = outs, PromptTokens = 5, CompletionTokens = 2,
            PrevHash = prev,
            EntryHash = AuditChainHasher.Compute(0, ts, "Legacy", "m", ins, outs, 5, 2, prev),
        };
        v1Entry.SchemaVersion.ShouldBe(1);
        v1Entry.TraceId.ShouldBe(""); // V1 row has no TraceId â†’ empty string default.

        var sink = new InMemoryAuditSink();
        await sink.AppendAsync(v1Entry, TestContext.Current.CancellationToken);

        var result = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task E2E_Activity_FallsBackToAmbientTraceId()
    {
        // Start an Activity to provide an ambient TraceId; verify AuditHook picks it up.
        using var source = new ActivitySource("SignalGrid.Ampelum.Test.TraceId");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("TestOperation");
        activity.ShouldNotBeNull();
        var ambientTraceId = activity!.TraceId.ToHexString();
        ambientTraceId.ShouldNotBeNullOrEmpty();

        var sink = new InMemoryAuditSink();
        var hook = new AuditHook(sink);
        // No AuditMetadataKeys.TraceId in metadata â†’ should fall back to Activity.Current.
        await hook.OnAfterAsync(MakeCtx(), TestContext.Current.CancellationToken);

        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries[0].TraceId.ShouldBe(ambientTraceId);

        // Chain must also verify with the ambient TraceId folded in.
        var result = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }
}

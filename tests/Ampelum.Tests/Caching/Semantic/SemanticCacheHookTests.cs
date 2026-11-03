using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheHookTests
{
    static HookContext Context(string moduleName = "Predict", IReadOnlyDictionary<string, object?>? inputs = null) =>
        new()
        {
            ModuleName = moduleName,
            Inputs = inputs ?? new Dictionary<string, object?> { ["Q"] = "what?" },
        };

    static IReadOnlyDictionary<string, object?> Outputs(string a) =>
        new Dictionary<string, object?> { ["A"] = a };

    [Fact]
    public void Order_IsAfterPiiRedactionHook_SoThatHookStillRunsOnAHit()
    {
        var hook = new SemanticCacheHook(new InMemorySemanticCache());
        hook.Order.ShouldBeGreaterThan(new SignalGrid.Ampelum.Safety.Pii.PiiRedactionHook(new SignalGrid.Ampelum.Safety.Pii.RegexPiiRedactor()).Order);
    }

    // Runs the hooks the way the pipeline does: before in ascending order until one supplies outputs,
    // then after in descending order.
    static async Task RunAsync(IHook[] hooks, HookContext ctx, Func<HookContext, Task> callModel)
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var hook in hooks.OrderBy(h => h.Order))
        {
            await hook.OnBeforeAsync(ctx, ct);
            if (ctx.Outputs is not null) break;
        }
        if (ctx.Outputs is null) await callModel(ctx);
        foreach (var hook in hooks.OrderByDescending(h => h.Order))
            await hook.OnAfterAsync(ctx, ct);
    }

    [Fact]
    public async Task Hit_StillRunsThePiiHook_SoTheAuditEntryHoldsTheRedactedInputs()
    {
        var cache = new InMemorySemanticCache();
        var sink = new InMemoryAuditSink();
        IHook[] hooks =
        [
            new SemanticCacheHook(cache),
            new AuditHook(sink),
            new SignalGrid.Ampelum.Safety.Pii.PiiRedactionHook(new SignalGrid.Ampelum.Safety.Pii.RegexPiiRedactor()),
        ];
        var question = new Dictionary<string, object?> { ["Q"] = "Mail jane.doe@example.com please" };

        await RunAsync(hooks, Context(inputs: question), ctx => { ctx.Outputs = Outputs("Sure."); return Task.CompletedTask; });
        await RunAsync(hooks, Context(inputs: question), _ => throw new InvalidOperationException("The second call must be a cache hit."));

        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) entries.Add(e);
        entries.Count.ShouldBe(2);
        entries[0].Inputs["Q"].ShouldBe("Mail <REDACTED:EMAIL> please");
        entries[1].Inputs["Q"].ShouldBe("Mail <REDACTED:EMAIL> please");
    }

    [Fact]
    public async Task Miss_StoresTheOutputsAsTheModelAnswered_NotTheRedactedCopy()
    {
        var cache = new InMemorySemanticCache();
        IHook[] hooks =
        [
            new SemanticCacheHook(cache),
            new SignalGrid.Ampelum.Safety.Pii.PiiRedactionHook(new SignalGrid.Ampelum.Safety.Pii.RegexPiiRedactor()),
        ];
        var question = new Dictionary<string, object?> { ["Q"] = "who?" };

        await RunAsync(hooks, Context(inputs: question), ctx => { ctx.Outputs = Outputs("Ask jane.doe@example.com"); return Task.CompletedTask; });

        cache.TryGet(SemanticCacheKey.Compute("Predict", question))!.Outputs["A"].ShouldBe("Ask jane.doe@example.com");
    }

    [Fact]
    public async Task OnBefore_Miss_DoesNothing()
    {
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var ctx = Context();

        await hook.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);

        ctx.Outputs.ShouldBeNull();
        ctx.Metadata.ContainsKey(HookMetadataKeys.SemanticCacheHit).ShouldBeFalse();
    }

    [Fact]
    public async Task OnAfter_PopulatesCache_ThenOnBefore_HitShortCircuits()
    {
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var ct = TestContext.Current.CancellationToken;

        // Cache-miss path
        var miss = Context();
        miss.Set(AuditMetadataKeys.ModelId, "gpt");
        miss.Set(HookMetadataKeys.PromptTokens, 11L);
        miss.Set(HookMetadataKeys.CompletionTokens, 22L);
        miss.Outputs = Outputs("Paris");
        await hook.OnAfterAsync(miss, ct);
        cache.Count.ShouldBe(1);

        // Cache-hit path: identical inputs â†’ short-circuit
        var hit = Context();
        await hook.OnBeforeAsync(hit, ct);
        hit.Outputs.ShouldNotBeNull();
        hit.Outputs!["A"].ShouldBe("Paris");
        hit.Metadata[AuditMetadataKeys.ModelId].ShouldBe("gpt");
        hit.Metadata[HookMetadataKeys.PromptTokens].ShouldBe(11L);
        hit.Metadata[HookMetadataKeys.CompletionTokens].ShouldBe(22L);
        hit.Metadata[HookMetadataKeys.SemanticCacheHit].ShouldBe(true);
    }

    [Fact]
    public async Task OnBefore_AuditReplayingFlag_BypassesCache()
    {
        var cache = new InMemorySemanticCache();
        cache.Set(SemanticCacheKey.Compute("Predict", new Dictionary<string, object?> { ["Q"] = "what?" }),
                  new SemanticCacheEntry(Outputs("Paris"), "m", 1, 2));
        var hook = new SemanticCacheHook(cache);
        var ctx = Context();
        ctx.Set(HookMetadataKeys.AuditReplaying, true);

        await hook.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);

        ctx.Outputs.ShouldBeNull(); // bypassed
    }

    [Fact]
    public async Task OnAfter_HitServedCalls_DoNotRewriteCache()
    {
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var ct = TestContext.Current.CancellationToken;

        // Pre-populate
        var miss = Context();
        miss.Outputs = Outputs("v1");
        await hook.OnAfterAsync(miss, ct);

        // Simulate a hit-served call: ctx has SemanticCacheHit metadata.
        var hit = Context();
        hit.Set(HookMetadataKeys.SemanticCacheHit, true);
        hit.Outputs = Outputs("v2"); // would otherwise overwrite
        await hook.OnAfterAsync(hit, ct);

        cache.TryGet(SemanticCacheKey.Compute("Predict", hit.Inputs))!.Outputs["A"].ShouldBe("v1");
    }

    [Fact]
    public async Task OnAfter_AuditReplaying_DoesNotPersist()
    {
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var ctx = Context();
        ctx.Set(HookMetadataKeys.AuditReplaying, true);
        ctx.Outputs = Outputs("ignored");

        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        cache.Count.ShouldBe(0);
    }

    [Fact]
    public async Task OnAfter_MissingOutputs_NoOp()
    {
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var ctx = Context();
        // ctx.Outputs stays null
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        cache.Count.ShouldBe(0);
    }

    [Fact]
    public async Task OnAfter_DefaultsModelIdToUnknownAndTokensToZero()
    {
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var ctx = Context();
        ctx.Outputs = Outputs("x");
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        var entry = cache.TryGet(SemanticCacheKey.Compute("Predict", ctx.Inputs))!;
        entry.ModelId.ShouldBe("unknown");
        entry.PromptTokens.ShouldBe(0L);
        entry.CompletionTokens.ShouldBe(0L);
    }

    [Fact]
    public async Task OnError_IsNoOp()
    {
        var hook = new SemanticCacheHook(new InMemorySemanticCache());
        await hook.OnErrorAsync(Context(), new InvalidOperationException("x"),
            TestContext.Current.CancellationToken);
        // no assertion needed: must-not-throw rule
    }

    [Fact]
    public void Constructor_NullCache_Throws() =>
        Should.Throw<ArgumentNullException>(() => new SemanticCacheHook(null!));

    [Fact]
    public async Task OnBeforeAsync_NullContext_Throws()
    {
        var hook = new SemanticCacheHook(new InMemorySemanticCache());
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await hook.OnBeforeAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnAfterAsync_NullContext_Throws()
    {
        var hook = new SemanticCacheHook(new InMemorySemanticCache());
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await hook.OnAfterAsync(null!, TestContext.Current.CancellationToken));
    }

    // â”€â”€ Determinism guard: cache hit produces audit entry semantically identical to miss â”€â”€

    [Fact]
    public async Task DeterminismGuard_HitEntryMatchesMissEntry()
    {
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var sink = new InMemoryAuditSink();
        var auditHook = new AuditHook(sink);
        var ct = TestContext.Current.CancellationToken;

        // -- Cache miss: SemanticCacheHook.OnAfter writes entry; AuditHook.OnAfter follows.
        var miss = Context();
        miss.Set(AuditMetadataKeys.ModelId, "gpt");
        miss.Set(HookMetadataKeys.PromptTokens, 11L);
        miss.Set(HookMetadataKeys.CompletionTokens, 22L);
        miss.Outputs = Outputs("Paris");
        await hook.OnAfterAsync(miss, ct);
        await auditHook.OnAfterAsync(miss, ct);

        // -- Cache hit: SemanticCacheHook.OnBefore restores metadata + outputs; AuditHook.OnAfter
        // should now produce a chain entry with the SAME (Inputs, Outputs, ModelId, tokens).
        var hit = Context();
        await hook.OnBeforeAsync(hit, ct);
        await auditHook.OnAfterAsync(hit, ct);

        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(ct)) entries.Add(e);
        entries.Count.ShouldBe(2);
        entries[0].Inputs.ShouldBe(entries[1].Inputs);
        entries[0].Outputs.ShouldBe(entries[1].Outputs);
        entries[0].ModelId.ShouldBe(entries[1].ModelId);
        entries[0].PromptTokens.ShouldBe(entries[1].PromptTokens);
        entries[0].CompletionTokens.ShouldBe(entries[1].CompletionTokens);
        entries[0].ModuleName.ShouldBe(entries[1].ModuleName);
    }
}

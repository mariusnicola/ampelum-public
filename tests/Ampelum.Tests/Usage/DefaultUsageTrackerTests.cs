using System.Threading.Tasks;
using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

public sealed class DefaultUsageTrackerTests
{
    // ── 7 spec tests (verbatim) ───────────────────────────────────────────

    [Fact]
    public void Record_IncrementsCounters()
    {
        var t = new DefaultUsageTracker();
        t.Record(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 2 }, cacheHit: false);
        var s = t.Snapshot();
        s.PromptTokens.ShouldBe(3);
        s.CompletionTokens.ShouldBe(2);
        s.TotalTokens.ShouldBe(5);
        s.RequestCount.ShouldBe(1);
        s.CacheHits.ShouldBe(0);
    }

    [Fact]
    public void Record_NullUsage_StillIncrementsRequestCount()
    {
        var t = new DefaultUsageTracker();
        t.Record(usage: null, cacheHit: false);
        t.Snapshot().RequestCount.ShouldBe(1);
        t.Snapshot().PromptTokens.ShouldBe(0);
    }

    [Fact]
    public void CacheHit_IncrementsHitCounter()
    {
        var t = new DefaultUsageTracker();
        t.Record(new UsageDetails(), cacheHit: true);
        t.Snapshot().CacheHits.ShouldBe(1);
    }

    [Fact]
    public void Reset_ClearsCounters()
    {
        var t = new DefaultUsageTracker();
        t.Record(new UsageDetails { InputTokenCount = 5 }, cacheHit: false);
        t.Reset();
        t.Snapshot().PromptTokens.ShouldBe(0);
        t.Snapshot().RequestCount.ShouldBe(0);
    }

    [Fact]
    public void Scope_AttributesCallsUnderScopeName()
    {
        var t = new DefaultUsageTracker();
        using (t.Scope("outer"))
            t.Record(new UsageDetails { InputTokenCount = 4 }, cacheHit: false);
        var s = t.Snapshot();
        s.PerScope!.ContainsKey("outer").ShouldBeTrue();
        s.PerScope["outer"].PromptTokens.ShouldBe(4);
    }

    [Fact]
    public void Scope_Nested_BothReceiveRecord()
    {
        var t = new DefaultUsageTracker();
        using (t.Scope("outer"))
        using (t.Scope("inner"))
            t.Record(new UsageDetails { InputTokenCount = 2 }, cacheHit: false);
        var s = t.Snapshot();
        s.PerScope!["outer"].PromptTokens.ShouldBe(2);
        s.PerScope["inner"].PromptTokens.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_Records_AreRaceFree()
    {
        var t = new DefaultUsageTracker();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            t.Record(new UsageDetails { InputTokenCount = 1 }, cacheHit: false))));
        t.Snapshot().PromptTokens.ShouldBe(100);
        t.Snapshot().RequestCount.ShouldBe(100);
    }

    // ── coverage extras ───────────────────────────────────────────────────

    [Fact]
    public void Scope_Null_ThrowsArgumentNullException()
    {
        var t = new DefaultUsageTracker();
        Should.Throw<ArgumentNullException>(() => t.Scope(null!));
    }

    [Fact]
    public void Popper_DoubleDispose_IsIdempotent()
    {
        var t = new DefaultUsageTracker();
        var scope = t.Scope("x");
        scope.Dispose();
        // second dispose must not throw and must not over-pop
        scope.Dispose();
        // stack is empty; record outside scope — PerScope should be null
        t.Record(new UsageDetails { InputTokenCount = 1 }, cacheHit: false);
        t.Snapshot().PerScope.ShouldBeNull();
    }

    [Fact]
    public void Record_UsageWithNullInputTokenCount_OnlyOutputCounted()
    {
        var t = new DefaultUsageTracker();
        // InputTokenCount left null, OutputTokenCount set
        t.Record(new UsageDetails { OutputTokenCount = 7 }, cacheHit: false);
        var s = t.Snapshot();
        s.PromptTokens.ShouldBe(0);
        s.CompletionTokens.ShouldBe(7);
        s.TotalTokens.ShouldBe(7);
        s.RequestCount.ShouldBe(1);
    }

    [Fact]
    public void Record_UsageWithNullOutputTokenCount_OnlyInputCounted()
    {
        var t = new DefaultUsageTracker();
        t.Record(new UsageDetails { InputTokenCount = 9 }, cacheHit: false);
        var s = t.Snapshot();
        s.PromptTokens.ShouldBe(9);
        s.CompletionTokens.ShouldBe(0);
        s.TotalTokens.ShouldBe(9);
    }

    [Fact]
    public void Reset_ClearsScopes()
    {
        var t = new DefaultUsageTracker();
        using (t.Scope("s"))
            t.Record(new UsageDetails { InputTokenCount = 1 }, cacheHit: false);
        t.Snapshot().PerScope.ShouldNotBeNull();
        t.Reset();
        t.Snapshot().PerScope.ShouldBeNull();
    }

    [Fact]
    public void Snapshot_NoScopes_PerScopeIsNull()
    {
        var t = new DefaultUsageTracker();
        t.Record(new UsageDetails { InputTokenCount = 1 }, cacheHit: false);
        t.Snapshot().PerScope.ShouldBeNull();
    }

    [Fact]
    public void CacheHit_InScope_ScopeCountsHit()
    {
        var t = new DefaultUsageTracker();
        using (t.Scope("s"))
            t.Record(new UsageDetails(), cacheHit: true);
        t.Snapshot().PerScope!["s"].CacheHits.ShouldBe(1);
    }

    [Fact]
    public void Scope_RecordAfterDispose_NotAttributedToScope()
    {
        var t = new DefaultUsageTracker();
        using (t.Scope("s"))
        {
            t.Record(new UsageDetails { InputTokenCount = 1 }, cacheHit: false);
        }
        // After scope disposed, this should NOT go to "s"
        t.Record(new UsageDetails { InputTokenCount = 5 }, cacheHit: false);
        t.Snapshot().PerScope!["s"].PromptTokens.ShouldBe(1);
        t.Snapshot().PromptTokens.ShouldBe(6);
    }

    [Fact]
    public void Scope_ScopeState_TotalTokens_IsSum()
    {
        var t = new DefaultUsageTracker();
        using (t.Scope("s"))
            t.Record(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4 }, cacheHit: false);
        t.Snapshot().PerScope!["s"].TotalTokens.ShouldBe(7);
    }

    [Fact]
    public void Scope_ScopeState_NullInputTokenCount_OnlyOutputCounted()
    {
        // Exercises the false branch of `if (u?.InputTokenCount is long ti)` inside ScopeState
        var t = new DefaultUsageTracker();
        using (t.Scope("s"))
            t.Record(new UsageDetails { OutputTokenCount = 5 }, cacheHit: false);
        var scope = t.Snapshot().PerScope!["s"];
        scope.PromptTokens.ShouldBe(0);
        scope.CompletionTokens.ShouldBe(5);
    }

    [Fact]
    public void Scope_ScopeState_NullOutputTokenCount_OnlyInputCounted()
    {
        // Exercises the false branch of `if (u?.OutputTokenCount is long to)` inside ScopeState
        var t = new DefaultUsageTracker();
        using (t.Scope("s"))
            t.Record(new UsageDetails { InputTokenCount = 6 }, cacheHit: false);
        var scope = t.Snapshot().PerScope!["s"];
        scope.PromptTokens.ShouldBe(6);
        scope.CompletionTokens.ShouldBe(0);
    }
}

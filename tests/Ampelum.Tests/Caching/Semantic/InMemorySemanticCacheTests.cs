using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Caching.Semantic;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class InMemorySemanticCacheTests
{
    static SemanticCacheEntry Entry(string outputValue) =>
        new(new Dictionary<string, object?> { ["A"] = outputValue }, "model", 1, 2);

    [Fact]
    public void TryGet_Miss_ReturnsNull()
    {
        var cache = new InMemorySemanticCache();
        cache.TryGet("absent").ShouldBeNull();
        cache.Count.ShouldBe(0);
    }

    [Fact]
    public void Set_ThenGet_RoundTrips()
    {
        var cache = new InMemorySemanticCache();
        cache.Set("k", Entry("v"));
        cache.Count.ShouldBe(1);
        cache.TryGet("k")!.Outputs["A"].ShouldBe("v");
    }

    [Fact]
    public void Set_OverwritesExistingKey()
    {
        var cache = new InMemorySemanticCache();
        cache.Set("k", Entry("v1"));
        cache.Set("k", Entry("v2"));
        cache.Count.ShouldBe(1);
        cache.TryGet("k")!.Outputs["A"].ShouldBe("v2");
    }

    [Fact]
    public void Capacity_EvictsLeastRecentlyUsed()
    {
        var cache = new InMemorySemanticCache(capacity: 2);
        cache.Set("a", Entry("a"));
        cache.Set("b", Entry("b"));
        cache.Set("c", Entry("c")); // evicts "a" (LRU)
        cache.TryGet("a").ShouldBeNull();
        cache.TryGet("b").ShouldNotBeNull();
        cache.TryGet("c").ShouldNotBeNull();
    }

    [Fact]
    public void TryGet_PromotesEntryToHead()
    {
        var cache = new InMemorySemanticCache(capacity: 2);
        cache.Set("a", Entry("a"));
        cache.Set("b", Entry("b"));
        cache.TryGet("a"); // promote a to MRU
        cache.Set("c", Entry("c")); // evicts b (now LRU), not a
        cache.TryGet("a").ShouldNotBeNull();
        cache.TryGet("b").ShouldBeNull();
        cache.TryGet("c").ShouldNotBeNull();
    }

    [Fact]
    public void Set_OverwriteAtTail_DoesNotAffectEviction()
    {
        // Set a, b — a is tail. Overwrite a (should move to head). Then set c — evicts b.
        var cache = new InMemorySemanticCache(capacity: 2);
        cache.Set("a", Entry("a"));
        cache.Set("b", Entry("b"));
        cache.Set("a", Entry("a2")); // overwrite + promote
        cache.Set("c", Entry("c"));  // evicts b
        cache.TryGet("a")!.Outputs["A"].ShouldBe("a2");
        cache.TryGet("b").ShouldBeNull();
        cache.TryGet("c").ShouldNotBeNull();
    }

    [Fact]
    public void CapacityOne_EvictsOnEveryNewKey()
    {
        // Exercises Unlink when the victim has Prev == null (head==tail) and Next == null.
        var cache = new InMemorySemanticCache(capacity: 1);
        cache.Set("a", Entry("a"));
        cache.Set("b", Entry("b")); // evicts a
        cache.TryGet("a").ShouldBeNull();
        cache.TryGet("b").ShouldNotBeNull();
        cache.Count.ShouldBe(1);
    }

    [Fact]
    public void Constructor_CapacityZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new InMemorySemanticCache(0));

    [Fact]
    public void TryGet_NullKey_Throws()
    {
        var cache = new InMemorySemanticCache();
        Should.Throw<ArgumentNullException>(() => cache.TryGet(null!));
    }

    [Fact]
    public void Set_NullKey_Throws()
    {
        var cache = new InMemorySemanticCache();
        Should.Throw<ArgumentNullException>(() => cache.Set(null!, Entry("v")));
    }

    [Fact]
    public void Set_NullEntry_Throws()
    {
        var cache = new InMemorySemanticCache();
        Should.Throw<ArgumentNullException>(() => cache.Set("k", null!));
    }

    [Fact]
    public void EntryRecord_FieldsRoundTrip()
    {
        var e = new SemanticCacheEntry(
            new Dictionary<string, object?> { ["X"] = 1 },
            "m",
            5,
            7);
        e.ModelId.ShouldBe("m");
        e.PromptTokens.ShouldBe(5L);
        e.CompletionTokens.ShouldBe(7L);
        e.Outputs["X"].ShouldBe(1);
    }
}

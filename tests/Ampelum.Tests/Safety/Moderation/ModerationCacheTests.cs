using SignalGrid.Ampelum.Safety.Moderation;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Moderation;

public sealed class ModerationCacheTests
{
    [Fact]
    public void TryGet_returns_false_when_empty()
    {
        var sut = new ModerationCache(8);
        sut.TryGet("absent", out var _).ShouldBeFalse();
    }

    [Fact]
    public void Set_then_TryGet_returns_value()
    {
        var sut = new ModerationCache(8);
        var verdict = new ModerationResult(true, new[] { "hate" }, Confidence: 0.9);
        sut.Set("hello", verdict);
        sut.TryGet("hello", out var got).ShouldBeTrue();
        got.ShouldBe(verdict);
    }

    [Fact]
    public void Set_overwrites_existing_entry()
    {
        var sut = new ModerationCache(8);
        sut.Set("k", new ModerationResult(false, System.Array.Empty<string>(), null));
        var second = new ModerationResult(true, new[] { "x" }, 0.5);
        sut.Set("k", second);
        sut.TryGet("k", out var got).ShouldBeTrue();
        got.ShouldBe(second);
    }

    [Fact]
    public void Capacity_overflow_evicts_least_recently_used()
    {
        var sut = new ModerationCache(2);
        var v1 = new ModerationResult(false, System.Array.Empty<string>(), null);
        var v2 = new ModerationResult(false, System.Array.Empty<string>(), null);
        var v3 = new ModerationResult(false, System.Array.Empty<string>(), null);
        sut.Set("a", v1);
        sut.Set("b", v2);
        sut.Set("c", v3); // evicts "a"
        sut.TryGet("a", out _).ShouldBeFalse();
        sut.TryGet("b", out _).ShouldBeTrue();
        sut.TryGet("c", out _).ShouldBeTrue();
    }

    [Fact]
    public void TryGet_promotes_recent_use_to_front_of_lru()
    {
        var sut = new ModerationCache(2);
        var v = new ModerationResult(false, System.Array.Empty<string>(), null);
        sut.Set("a", v);
        sut.Set("b", v);
        sut.TryGet("a", out _).ShouldBeTrue();   // promote "a"
        sut.Set("c", v);                           // should evict "b" now, not "a"
        sut.TryGet("a", out _).ShouldBeTrue();
        sut.TryGet("b", out _).ShouldBeFalse();
    }
}

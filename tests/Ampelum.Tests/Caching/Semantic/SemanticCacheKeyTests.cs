using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Caching.Semantic;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheKeyTests
{
    [Fact]
    public void Same_Module_SameInputs_SameKey()
    {
        var a = SemanticCacheKey.Compute("M", new Dictionary<string, object?> { ["x"] = "hi" });
        var b = SemanticCacheKey.Compute("M", new Dictionary<string, object?> { ["x"] = "hi" });
        a.ShouldBe(b);
    }

    [Fact]
    public void Different_Module_DifferentKey()
    {
        var a = SemanticCacheKey.Compute("A", new Dictionary<string, object?> { ["x"] = 1 });
        var b = SemanticCacheKey.Compute("B", new Dictionary<string, object?> { ["x"] = 1 });
        a.ShouldNotBe(b);
    }

    [Fact]
    public void DifferentInputValues_DifferentKey()
    {
        var a = SemanticCacheKey.Compute("M", new Dictionary<string, object?> { ["x"] = "a" });
        var b = SemanticCacheKey.Compute("M", new Dictionary<string, object?> { ["x"] = "b" });
        a.ShouldNotBe(b);
    }

    [Fact]
    public void KeyOrderInDictionary_DoesNotAffectHash()
    {
        var a = SemanticCacheKey.Compute("M",
            new Dictionary<string, object?> { ["x"] = 1, ["y"] = 2 });
        var b = SemanticCacheKey.Compute("M",
            new Dictionary<string, object?> { ["y"] = 2, ["x"] = 1 });
        a.ShouldBe(b);
    }

    [Fact]
    public void Handles_AllPrimitiveCategories()
    {
        var key = SemanticCacheKey.Compute("M", new Dictionary<string, object?>
        {
            ["s"] = "text",
            ["b"] = true,
            ["i"] = 42,
            ["d"] = 3.14,
            ["nil"] = null,
            ["o"] = (object)new Wrapper(7),
        });
        key.Length.ShouldBe(64); // SHA-256 hex
    }

    sealed record Wrapper(int X)
    {
        public override string ToString() => X.ToString();
    }
}

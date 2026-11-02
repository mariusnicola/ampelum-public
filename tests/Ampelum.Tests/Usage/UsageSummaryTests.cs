using SignalGrid.Ampelum.Usage;
using Shouldly;
using Xunit;

public sealed class UsageSummaryTests
{
    [Fact]
    public void Construct_ExposesAllFields()
    {
        var s = new UsageSummary(10, 5, 15, 2, 1, null);
        s.PromptTokens.ShouldBe(10);
        s.CompletionTokens.ShouldBe(5);
        s.TotalTokens.ShouldBe(15);
        s.RequestCount.ShouldBe(2);
        s.CacheHits.ShouldBe(1);
        s.PerScope.ShouldBeNull();
    }

    [Fact]
    public void Construct_WithPerScope_ExposesDict()
    {
        var inner = new UsageSummary(1, 2, 3, 1, 0, null);
        var dict = new Dictionary<string, UsageSummary> { ["a"] = inner };
        var s = new UsageSummary(1, 2, 3, 1, 0, dict);
        s.PerScope.ShouldNotBeNull();
        s.PerScope!["a"].ShouldBe(inner);
    }

    [Fact]
    public void Record_Equality_SameValues_AreEqual()
    {
        var a = new UsageSummary(10, 5, 15, 2, 1, null);
        var b = new UsageSummary(10, 5, 15, 2, 1, null);
        a.ShouldBe(b);
    }

    [Fact]
    public void Record_Equality_DifferentValues_NotEqual()
    {
        var a = new UsageSummary(10, 5, 15, 2, 1, null);
        var b = new UsageSummary(10, 5, 15, 2, 0, null);
        a.ShouldNotBe(b);
    }
}

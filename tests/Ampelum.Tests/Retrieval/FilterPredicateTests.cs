using SignalGrid.Ampelum.Retrieval.Filtering;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class FilterPredicateTests
{
    private static IReadOnlyDictionary<string, object?> Meta(params (string k, object? v)[] kvs)
        => kvs.ToDictionary(t => t.k, t => t.v);

    [Fact] public void Equals_MatchesValue() => FilterPredicate.Equals("k", "v").Matches(Meta(("k", "v"))).ShouldBeTrue();
    [Fact] public void Equals_RejectsMismatch() => FilterPredicate.Equals("k", "v").Matches(Meta(("k", "x"))).ShouldBeFalse();
    [Fact] public void Equals_RejectsMissingKey() => FilterPredicate.Equals("k", "v").Matches(Meta()).ShouldBeFalse();
    [Fact] public void In_MatchesAny() => FilterPredicate.In("k", ["a", "b"]).Matches(Meta(("k", "b"))).ShouldBeTrue();
    [Fact] public void In_RejectsNotInSet() => FilterPredicate.In("k", ["a", "b"]).Matches(Meta(("k", "c"))).ShouldBeFalse();
    [Fact] public void In_RejectsMissingKey() => FilterPredicate.In("k", ["a", "b"]).Matches(Meta()).ShouldBeFalse();
    [Fact] public void And_RequiresAll() => FilterPredicate.And(FilterPredicate.Equals("a", 1), FilterPredicate.Equals("b", 2)).Matches(Meta(("a", 1), ("b", 2))).ShouldBeTrue();
    [Fact] public void And_RejectsAnyMiss() => FilterPredicate.And(FilterPredicate.Equals("a", 1), FilterPredicate.Equals("b", 2)).Matches(Meta(("a", 1), ("b", 9))).ShouldBeFalse();
    [Fact] public void Or_AcceptsAny() => FilterPredicate.Or(FilterPredicate.Equals("a", 1), FilterPredicate.Equals("b", 2)).Matches(Meta(("b", 2))).ShouldBeTrue();
    [Fact] public void Or_RejectsAllMiss() => FilterPredicate.Or(FilterPredicate.Equals("a", 1), FilterPredicate.Equals("b", 2)).Matches(Meta(("a", 9), ("b", 9))).ShouldBeFalse();

    [Fact]
    public void ToDictionary_FlattensEqualsAnd()
    {
        var p = FilterPredicate.And(FilterPredicate.Equals("a", 1), FilterPredicate.Equals("b", "x"));
        var dict = p.ToFlatEqualsDictionary();
        dict.ShouldNotBeNull();
        dict!["a"].ShouldBe(1);
        dict["b"].ShouldBe("x");
    }

    [Fact]
    public void ToDictionary_FlatEquals_ForSingleEquals()
    {
        var dict = FilterPredicate.Equals("k", "v").ToFlatEqualsDictionary();
        dict.ShouldNotBeNull();
        dict!["k"].ShouldBe("v");
    }

    [Fact]
    public void ToDictionary_NullForOr()
    {
        var p = FilterPredicate.Or(FilterPredicate.Equals("a", 1), FilterPredicate.Equals("b", 2));
        p.ToFlatEqualsDictionary().ShouldBeNull();
    }

    [Fact]
    public void ToDictionary_NullForIn()
    {
        FilterPredicate.In("k", ["a"]).ToFlatEqualsDictionary().ShouldBeNull();
    }

    [Fact]
    public void ToDictionary_NullWhenAndContainsNonFlat()
    {
        var p = FilterPredicate.And(FilterPredicate.Equals("a", 1), FilterPredicate.In("b", ["x"]));
        p.ToFlatEqualsDictionary().ShouldBeNull();
    }
}

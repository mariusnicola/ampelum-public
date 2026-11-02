using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class MetricRegistryTests
{
    sealed record QA : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string A { get; init; } = "";
    }

    [Fact]
    public void Add_Named_Stores_By_Name()
    {
        var em = new ExactMatch<QA>("A");
        var r = new MetricRegistryBuilder().Add(em).Build();
        r.Get(em.Name).ShouldBeSameAs(em);
    }

    [Fact]
    public void Add_Explicit_Name_Stores()
    {
        var em = new ExactMatch<QA>("A");
        var r = new MetricRegistryBuilder().Add("custom", em).Build();
        r.Get("custom").ShouldBeSameAs(em);
    }

    [Fact]
    public void Add_Duplicate_Named_Throws()
    {
        var b = new MetricRegistryBuilder().Add(new ExactMatch<QA>("A"));
        Should.Throw<ArgumentException>(() => b.Add(new ExactMatch<QA>("A")));
    }

    [Fact]
    public void Add_Duplicate_Explicit_Throws()
    {
        var b = new MetricRegistryBuilder().Add("k", new ExactMatch<QA>("A"));
        Should.Throw<ArgumentException>(() => b.Add("k", new ExactMatch<QA>("A")));
    }

    [Fact]
    public void Add_Null_Named_Throws() =>
        Should.Throw<ArgumentNullException>(() => new MetricRegistryBuilder().Add((INamedMetric)null!));

    [Fact]
    public void Add_Null_Explicit_Throws()
    {
        var b = new MetricRegistryBuilder();
        Should.Throw<ArgumentException>(() => b.Add("", new ExactMatch<QA>("A")));
        Should.Throw<ArgumentNullException>(() => b.Add("k", null!));
    }

    [Fact]
    public void Get_Missing_Throws_Lists_Available_Names()
    {
        var r = new MetricRegistryBuilder().Add(new ExactMatch<QA>("A")).Build();
        var ex = Should.Throw<KeyNotFoundException>(() => r.Get("missing"));
        ex.Message.ShouldContain("exact_match[A]");
    }

    [Fact]
    public void Get_NullOrEmpty_Throws()
    {
        var r = new MetricRegistryBuilder().Build();
        Should.Throw<ArgumentException>(() => r.Get(""));
    }

    [Fact]
    public void TryGet_Hit_Returns_True()
    {
        var em = new ExactMatch<QA>("A");
        var r = new MetricRegistryBuilder().Add(em).Build();
        r.TryGet(em.Name, out var got).ShouldBeTrue();
        got.ShouldBeSameAs(em);
    }

    [Fact]
    public void TryGet_Missing_Returns_False_NullOut()
    {
        var r = new MetricRegistryBuilder().Build();
        r.TryGet("nope", out var got).ShouldBeFalse();
        got.ShouldBeNull();
    }

    [Fact]
    public void TryGet_NullOrEmpty_Throws()
    {
        var r = new MetricRegistryBuilder().Build();
        Should.Throw<ArgumentException>(() => r.TryGet("", out _));
    }

    [Fact]
    public void Names_Snapshot_Lists_All()
    {
        var r = new MetricRegistryBuilder()
            .Add(new ExactMatch<QA>("A"))
            .Add("custom", new F1<QA>("A"))
            .Build();
        r.Names.Count.ShouldBe(2);
        r.Names.ShouldContain("exact_match[A]");
        r.Names.ShouldContain("custom");
    }

    [Fact]
    public void Ctor_NullEntries_Throws() =>
        Should.Throw<ArgumentNullException>(() => new MetricRegistry(null!));
}

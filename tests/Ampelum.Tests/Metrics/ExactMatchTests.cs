using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class ExactMatchTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    sealed record QAInt : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public int Answer { get; init; } = 0;
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();
    static readonly SignatureMetadata MetaInt = SignatureMetadataCache.For<QAInt>();

    [Fact]
    public void Equal_Strings_Score_One() =>
        new ExactMatch<QA>("Answer").Score(new QA { Answer = "x" }, new QA { Answer = "x" }, Meta).ShouldBe(1.0);

    [Fact]
    public void Different_Strings_Score_Zero() =>
        new ExactMatch<QA>("Answer").Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta).ShouldBe(0.0);

    [Fact]
    public void CaseSensitive_ByDefault() =>
        new ExactMatch<QA>("Answer").Score(new QA { Answer = "X" }, new QA { Answer = "x" }, Meta).ShouldBe(0.0);

    [Fact]
    public void NullField_BothNull_Scores_One() =>
        new ExactMatch<QA>("Answer").Score(new QA(), new QA(), Meta).ShouldBe(1.0);

    [Fact]
    public void MissingField_Throws()
    {
        var m = new ExactMatch<QA>("Missing");
        Should.Throw<ArgumentException>(() => m.Score(new QA(), new QA(), Meta))
              .Message.ShouldContain("Missing");
    }

    [Fact]
    public void Equal_NonStringFields_Score_One() =>
        new ExactMatch<QAInt>("Answer").Score(new QAInt { Answer = 42 }, new QAInt { Answer = 42 }, MetaInt).ShouldBe(1.0);

    [Fact]
    public void Different_NonStringFields_Score_Zero() =>
        new ExactMatch<QAInt>("Answer").Score(new QAInt { Answer = 42 }, new QAInt { Answer = 43 }, MetaInt).ShouldBe(0.0);
}

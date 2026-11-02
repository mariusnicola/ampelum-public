using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class CaseInsensitiveMatchTests
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
    public void DifferentCase_Scores_One() =>
        new CaseInsensitiveMatch<QA>("Answer").Score(new QA { Answer = "Paris" }, new QA { Answer = "paris" }, Meta).ShouldBe(1.0);

    [Fact]
    public void DifferentValue_Scores_Zero() =>
        new CaseInsensitiveMatch<QA>("Answer").Score(new QA { Answer = "Rome" }, new QA { Answer = "Paris" }, Meta).ShouldBe(0.0);

    [Fact]
    public void NonString_FallsBackToEquals_Matching() =>
        new CaseInsensitiveMatch<QAInt>("Answer").Score(new QAInt { Answer = 42 }, new QAInt { Answer = 42 }, MetaInt).ShouldBe(1.0);

    [Fact]
    public void NonString_FallsBackToEquals_NotMatching() =>
        new CaseInsensitiveMatch<QAInt>("Answer").Score(new QAInt { Answer = 42 }, new QAInt { Answer = 43 }, MetaInt).ShouldBe(0.0);

    [Fact]
    public void MissingField_Throws()
    {
        var m = new CaseInsensitiveMatch<QA>("Missing");
        Should.Throw<ArgumentException>(() => m.Score(new QA(), new QA(), Meta))
              .Message.ShouldContain("Missing");
    }
}

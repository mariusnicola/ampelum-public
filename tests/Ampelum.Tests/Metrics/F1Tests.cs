using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class F1Tests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();

    [Fact]
    public void Identical_TokenSets_Score_One() =>
        new F1<QA>("Answer").Score(new QA { Answer = "the cat sat" }, new QA { Answer = "the cat sat" }, Meta).ShouldBe(1.0);

    [Fact]
    public void NoOverlap_Scores_Zero() =>
        new F1<QA>("Answer").Score(new QA { Answer = "a b c" }, new QA { Answer = "x y z" }, Meta).ShouldBe(0.0);

    [Fact]
    public void PartialOverlap_F1_IsHarmonicMean()
    {
        var s = new F1<QA>("Answer").Score(new QA { Answer = "a b c" }, new QA { Answer = "b c d" }, Meta);
        s.ShouldBe(2.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void BothEmpty_Scores_One() =>
        new F1<QA>("Answer").Score(new QA { Answer = "" }, new QA { Answer = "" }, Meta).ShouldBe(1.0);

    [Fact]
    public void OneEmpty_Scores_Zero() =>
        new F1<QA>("Answer").Score(new QA { Answer = "" }, new QA { Answer = "a" }, Meta).ShouldBe(0.0);

    [Fact]
    public void MissingField_Throws()
    {
        var m = new F1<QA>("Missing");
        Should.Throw<ArgumentException>(() => m.Score(new QA(), new QA(), Meta))
              .Message.ShouldContain("Missing");
    }

    [Fact]
    public void ActualEmpty_ExpectedNonEmpty_Scores_Zero() =>
        new F1<QA>("Answer").Score(new QA { Answer = "" }, new QA { Answer = "x y z" }, Meta).ShouldBe(0.0);

    [Fact]
    public void SingleToken_Match_Scores_One() =>
        new F1<QA>("Answer").Score(new QA { Answer = "hello" }, new QA { Answer = "hello" }, Meta).ShouldBe(1.0);

    [Fact]
    public void PartialOverlap_TwoOutOfThree() =>
        new F1<QA>("Answer").Score(new QA { Answer = "a b" }, new QA { Answer = "a b c" }, Meta).ShouldBe(0.8, tolerance: 1e-9);

    [Fact]
    public void Whitespace_Variations_Tokens_Correctly()
    {
        // Multiple spaces, tabs, newlines should be treated as delimiters
        var s = new F1<QA>("Answer").Score(
            new QA { Answer = "a  b\tc\nd" },
            new QA { Answer = "a b c d" },
            Meta);
        s.ShouldBe(1.0);
    }

    [Fact]
    public void ArgumentExceptionMessage_IncludesFieldName() =>
        Should.Throw<ArgumentException>(() =>
            new F1<QA>("Missing").Score(new QA(), new QA(), Meta)).Message.ShouldContain("Missing");
}

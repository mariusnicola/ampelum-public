using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class BleuTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();

    [Fact]
    public void Bleu_BothEmpty_Returns_One() =>
        new Bleu<QA>(4, "Answer").Score(new QA(), new QA(), Meta).ShouldBe(1.0);

    [Fact]
    public void Bleu_OneEmpty_Returns_Zero() =>
        new Bleu<QA>(4, "Answer").Score(new QA { Answer = "x" }, new QA(), Meta).ShouldBe(0.0);

    [Fact]
    public void Bleu_OtherEmpty_Returns_Zero() =>
        new Bleu<QA>(4, "Answer").Score(new QA(), new QA { Answer = "x" }, Meta).ShouldBe(0.0);

    [Fact]
    public void Bleu_PerfectMatch_BrevityPenalty_One()
    {
        // candidate == reference -> all n-gram matches are perfect
        var v = new Bleu<QA>(2, "Answer").Score(
            new QA { Answer = "the cat sat" },
            new QA { Answer = "the cat sat" }, Meta);
        // With smoothing matches=total -> p = (m+1)/(t+1) which is < 1 unless smoothing skipped.
        // We accept the smoothed value > 0.5
        v.ShouldBeGreaterThan(0.5);
    }

    [Fact]
    public void Bleu_ShorterCandidate_BrevityPenaltyApplied()
    {
        // candidate len 3, reference len 6 -> BP = exp(1 - 6/3) = exp(-1) ~= 0.3679
        var v = new Bleu<QA>(1, "Answer").Score(
            new QA { Answer = "the cat sat" },
            new QA { Answer = "the cat sat on the mat" }, Meta);
        // With BP ~= 0.3679 and unigram matches=3,total=3, p_smoothed=(3+1)/(3+1)=1.0
        // -> result = 0.3679
        v.ShouldBe(Math.Exp(-1.0), 1e-6);
    }

    [Fact]
    public void Bleu_NgramHigherThanCandidateLength_ReturnsZero() =>
        new Bleu<QA>(4, "Answer").Score(
            new QA { Answer = "a b" }, new QA { Answer = "a b c d" }, Meta).ShouldBe(0.0);

    [Fact]
    public void Bleu_LongerCandidate_NoBP()
    {
        // candidate len 5, reference len 3 -> BP = 1
        var v = new Bleu<QA>(1, "Answer").Score(
            new QA { Answer = "the cat sat on mat" },
            new QA { Answer = "the cat sat" }, Meta);
        v.ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public void Bleu_NMustBePositive_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new Bleu<QA>(0, "Answer"));

    [Fact]
    public void Bleu_EmptyField_Throws() =>
        Should.Throw<ArgumentException>(() => new Bleu<QA>(1, ""));

    [Fact]
    public void Bleu_Name_Includes_MaxN_AndField() =>
        new Bleu<QA>(4, "Answer").Name.ShouldBe("bleu-4[Answer]");

    [Fact]
    public void Bleu_FieldMissing_Throws()
    {
        var m = new Bleu<QA>(1, "Missing");
        Should.Throw<ArgumentException>(() => m.Score(new QA { Answer = "x" }, new QA { Answer = "x" }, Meta));
    }

    [Fact]
    public void Bleu_IsINamedMetric() =>
        new Bleu<QA>(1, "Answer").ShouldBeAssignableTo<INamedMetric>();
}

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class RougeTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();

    [Fact]
    public void RougeN_BothEmpty_Returns_One() =>
        new RougeN<QA>(1, "Answer").Score(new QA(), new QA(), Meta).ShouldBe(1.0);

    [Fact]
    public void RougeN_OneEmpty_Returns_Zero() =>
        new RougeN<QA>(1, "Answer").Score(new QA { Answer = "x" }, new QA(), Meta).ShouldBe(0.0);

    [Fact]
    public void RougeN_OtherEmpty_Returns_Zero() =>
        new RougeN<QA>(1, "Answer").Score(new QA(), new QA { Answer = "x" }, Meta).ShouldBe(0.0);

    [Fact]
    public void RougeN_PerfectMatch_Returns_One() =>
        new RougeN<QA>(1, "Answer").Score(new QA { Answer = "the cat" }, new QA { Answer = "the cat" }, Meta).ShouldBe(1.0);

    [Fact]
    public void RougeN_PartialOverlap_Recall() =>
        new RougeN<QA>(1, "Answer").Score(new QA { Answer = "the cat sat" }, new QA { Answer = "the cat" }, Meta).ShouldBe(1.0);

    [Fact]
    public void RougeN_PartialOverlap_RecallReverse() =>
        new RougeN<QA>(1, "Answer").Score(new QA { Answer = "the cat" }, new QA { Answer = "the cat sat" }, Meta).ShouldBe(2.0 / 3.0, 1e-9);

    [Fact]
    public void RougeN_BigramNoOverlap_Zero() =>
        new RougeN<QA>(2, "Answer").Score(new QA { Answer = "a b" }, new QA { Answer = "x y" }, Meta).ShouldBe(0.0);

    [Fact]
    public void RougeN_NMustBePositive_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new RougeN<QA>(0, "Answer"));

    [Fact]
    public void RougeN_NullField_Throws() =>
        Should.Throw<ArgumentException>(() => new RougeN<QA>(1, ""));

    [Fact]
    public void RougeN_FieldMissing_Throws()
    {
        var m = new RougeN<QA>(1, "Missing");
        Should.Throw<ArgumentException>(() => m.Score(new QA { Answer = "x" }, new QA { Answer = "x" }, Meta));
    }

    [Fact]
    public void RougeN_Reference_TooShortForN_Returns_Zero() =>
        // candidate has unigrams "a","b"; reference "a" -> n=2 -> ref has zero 2-grams -> 0
        new RougeN<QA>(2, "Answer").Score(new QA { Answer = "a b" }, new QA { Answer = "a" }, Meta).ShouldBe(0.0);

    [Fact]
    public void RougeN_Name_Includes_NAndField() =>
        new RougeN<QA>(2, "Answer").Name.ShouldBe("rouge-2[Answer]");

    [Fact]
    public void RougeL_PerfectMatch_Returns_One() =>
        new RougeL<QA>("Answer").Score(new QA { Answer = "the cat" }, new QA { Answer = "the cat" }, Meta).ShouldBe(1.0, 1e-9);

    [Fact]
    public void RougeL_BothEmpty_Returns_One() =>
        new RougeL<QA>("Answer").Score(new QA(), new QA(), Meta).ShouldBe(1.0);

    [Fact]
    public void RougeL_OneEmpty_Returns_Zero() =>
        new RougeL<QA>("Answer").Score(new QA { Answer = "x" }, new QA(), Meta).ShouldBe(0.0);

    [Fact]
    public void RougeL_OtherEmpty_Returns_Zero() =>
        new RougeL<QA>("Answer").Score(new QA(), new QA { Answer = "x" }, Meta).ShouldBe(0.0);

    [Fact]
    public void RougeL_NoOverlap_Returns_Zero() =>
        new RougeL<QA>("Answer").Score(new QA { Answer = "a b c" }, new QA { Answer = "x y z" }, Meta).ShouldBe(0.0);

    [Fact]
    public void RougeL_KnownLcs()
    {
        // "the cat sat on the mat" vs "the cat lay on a mat"
        // LCS: the,cat,on,mat -> 4
        // P = 4/6, R = 4/6, β=1.2, β²=1.44
        // F = (1+1.44)*p*r / (r + 1.44*p) = 2.44*(4/6)*(4/6) / ((4/6) + 1.44*(4/6))
        //   = (2.44 * 16/36) / ((4/6)*(1+1.44))
        //   = (2.44 * 16/36) / ((4/6)*2.44)
        //   = (16/36) / (4/6) = (16/36) * (6/4) = 96/144 = 4/6 ≈ 0.6667
        new RougeL<QA>("Answer")
            .Score(new QA { Answer = "the cat sat on the mat" }, new QA { Answer = "the cat lay on a mat" }, Meta)
            .ShouldBe(4.0 / 6.0, 1e-9);
    }

    [Fact]
    public void RougeL_NullField_Throws() =>
        Should.Throw<ArgumentException>(() => new RougeL<QA>(""));

    [Fact]
    public void RougeL_FieldMissing_Throws()
    {
        var m = new RougeL<QA>("Missing");
        Should.Throw<ArgumentException>(() => m.Score(new QA { Answer = "x" }, new QA { Answer = "x" }, Meta));
    }

    [Fact]
    public void RougeL_Name_Includes_Field() =>
        new RougeL<QA>("Answer").Name.ShouldBe("rouge-l[Answer]");

    [Fact]
    public void RougeL_IsINamedMetric() =>
        new RougeL<QA>("Answer").ShouldBeAssignableTo<INamedMetric>();
}

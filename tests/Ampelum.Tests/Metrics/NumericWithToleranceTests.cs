using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class NumericWithToleranceTests
{
    sealed record Num : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public double V { get; init; }
    }

    sealed record NumInt : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public int V { get; init; }
    }

    sealed record NumLong : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public long V { get; init; }
    }

    sealed record NumFloat : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public float V { get; init; }
    }

    sealed record NumWithNullableDouble : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public double? V { get; init; }
    }

    sealed record NumWithString : ISignature
    {
        [Input] public string Q { get; init; } = "";
        [Output] public string V { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<Num>();
    static readonly SignatureMetadata MetaInt = SignatureMetadataCache.For<NumInt>();
    static readonly SignatureMetadata MetaLong = SignatureMetadataCache.For<NumLong>();
    static readonly SignatureMetadata MetaFloat = SignatureMetadataCache.For<NumFloat>();
    static readonly SignatureMetadata MetaNullable = SignatureMetadataCache.For<NumWithNullableDouble>();
    static readonly SignatureMetadata MetaString = SignatureMetadataCache.For<NumWithString>();

    [Fact]
    public void WithinTolerance_Scores_One() =>
        new NumericWithTolerance<Num>("V", tolerance: 0.1).Score(new Num { V = 1.05 }, new Num { V = 1.0 }, Meta).ShouldBe(1.0);

    [Fact]
    public void OutsideTolerance_Scores_Zero() =>
        new NumericWithTolerance<Num>("V", tolerance: 0.1).Score(new Num { V = 1.5 }, new Num { V = 1.0 }, Meta).ShouldBe(0.0);

    [Fact]
    public void NonNumericField_Throws()
    {
        var m = new NumericWithTolerance<Num>("Q", tolerance: 0.0);
        Should.Throw<InvalidOperationException>(() => m.Score(new Num { Q = "x" }, new Num { Q = "x" }, Meta));
    }

    [Fact]
    public void MissingField_Throws()
    {
        var m = new NumericWithTolerance<Num>("Missing", tolerance: 0.0);
        Should.Throw<ArgumentException>(() => m.Score(new Num(), new Num(), Meta))
              .Message.ShouldContain("Missing");
    }


    [Fact]
    public void IntField_WithinTolerance_Scores_One() =>
        new NumericWithTolerance<NumInt>("V", tolerance: 0.5).Score(new NumInt { V = 10 }, new NumInt { V = 10 }, MetaInt).ShouldBe(1.0);

    [Fact]
    public void IntField_OutsideTolerance_Scores_Zero() =>
        new NumericWithTolerance<NumInt>("V", tolerance: 0.5).Score(new NumInt { V = 11 }, new NumInt { V = 10 }, MetaInt).ShouldBe(0.0);

    [Fact]
    public void LongField_WithinTolerance_Scores_One() =>
        new NumericWithTolerance<NumLong>("V", tolerance: 1.0).Score(new NumLong { V = 100L }, new NumLong { V = 100L }, MetaLong).ShouldBe(1.0);

    [Fact]
    public void LongField_OutsideTolerance_Scores_Zero() =>
        new NumericWithTolerance<NumLong>("V", tolerance: 0.5).Score(new NumLong { V = 101L }, new NumLong { V = 100L }, MetaLong).ShouldBe(0.0);

    [Fact]
    public void FloatField_WithinTolerance_Scores_One() =>
        new NumericWithTolerance<NumFloat>("V", tolerance: 0.1f).Score(new NumFloat { V = 1.05f }, new NumFloat { V = 1.0f }, MetaFloat).ShouldBe(1.0);

    [Fact]
    public void FloatField_OutsideTolerance_Scores_Zero() =>
        new NumericWithTolerance<NumFloat>("V", tolerance: 0.1f).Score(new NumFloat { V = 1.5f }, new NumFloat { V = 1.0f }, MetaFloat).ShouldBe(0.0);


    [Fact]
    public void ExactMatch_WithinZeroTolerance_Scores_One() =>
        new NumericWithTolerance<Num>("V", tolerance: 0.0).Score(new Num { V = 1.0 }, new Num { V = 1.0 }, Meta).ShouldBe(1.0);

    [Fact]
    public void NegativeValues_WithinTolerance_Scores_One() =>
        new NumericWithTolerance<Num>("V", tolerance: 0.2).Score(new Num { V = -1.1 }, new Num { V = -1.0 }, Meta).ShouldBe(1.0);

    [Fact]
    public void ActualNull_Throws() =>
        Should.Throw<InvalidOperationException>(() =>
            new NumericWithTolerance<NumWithNullableDouble>("V", tolerance: 0.0).Score(
                new NumWithNullableDouble { V = null },
                new NumWithNullableDouble { V = 1.0 },
                MetaNullable)).Message.ShouldContain("null");

    [Fact]
    public void ExpectedNull_Throws() =>
        Should.Throw<InvalidOperationException>(() =>
            new NumericWithTolerance<NumWithNullableDouble>("V", tolerance: 0.0).Score(
                new NumWithNullableDouble { V = 1.0 },
                new NumWithNullableDouble { V = null },
                MetaNullable)).Message.ShouldContain("null");

    [Fact]
    public void NonNumericString_Throws() =>
        Should.Throw<InvalidOperationException>(() =>
            new NumericWithTolerance<NumWithString>("V", tolerance: 0.0).Score(
                new NumWithString { V = "abc" },
                new NumWithString { V = "xyz" },
                MetaString)).Message.ShouldContain("non-numeric");
}

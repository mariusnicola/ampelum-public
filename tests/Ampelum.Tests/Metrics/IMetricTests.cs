using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class IMetricTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    sealed class AlwaysHalf : IMetric<QA>
    {
        public double Score(QA actual, QA expected, SignatureMetadata signature) => 0.5;
    }

    [Fact]
    public void DefaultInterfaceMember_ForwardsUntypedCall_ToTyped()
    {
        IMetric untyped = new AlwaysHalf();
        var a = new QA { Answer = "a" };
        var b = new QA { Answer = "b" };
        var meta = SignatureMetadataCache.For<QA>();
        untyped.Score(a, b, meta).ShouldBe(0.5);
    }

    [Fact]
#pragma warning disable CS0184 // Type check is always false
    public void IRefinableFeedback_IsOptIn_NotRequired() =>
        (new AlwaysHalf() is IRefinableFeedback).ShouldBeFalse();
#pragma warning restore CS0184
}

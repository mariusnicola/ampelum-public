using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Shouldly;
using Xunit;

public sealed class UserDefinedTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();

    [Fact]
    public void Score_InvokesDelegate() =>
        new UserDefined<QA>((a, e) => a.Answer == e.Answer ? 0.5 : 0.25)
            .Score(new QA { Answer = "x" }, new QA { Answer = "x" }, Meta)
            .ShouldBe(0.5);

    [Fact]
    public void Score_InvokesDelegate_Mismatch() =>
        new UserDefined<QA>((a, e) => a.Answer == e.Answer ? 0.5 : 0.25)
            .Score(new QA { Answer = "x" }, new QA { Answer = "y" }, Meta)
            .ShouldBe(0.25);

    [Fact]
    public void Score_ReturnsOne() =>
        new UserDefined<QA>((_, _) => 1.0)
            .Score(new QA { Answer = "anything" }, new QA { Answer = "anything" }, Meta)
            .ShouldBe(1.0);

    [Fact]
    public void Score_ReturnsZero() =>
        new UserDefined<QA>((_, _) => 0.0)
            .Score(new QA { Answer = "x" }, new QA { Answer = "y" }, Meta)
            .ShouldBe(0.0);

    [Fact]
    public void Score_CustomLogic_ComplexComparison() =>
        new UserDefined<QA>((a, e) =>
        {
            var lenMatch = Math.Abs(a.Answer.Length - e.Answer.Length) <= 2 ? 0.5 : 0.0;
            var contentMatch = a.Answer.StartsWith(e.Answer[0]) ? 0.5 : 0.0;
            return lenMatch + contentMatch;
        })
        .Score(new QA { Answer = "hello" }, new QA { Answer = "hey" }, Meta)
        .ShouldBe(1.0);

    [Fact]
    public void Score_DelegateAccessesBothParameters() =>
        new UserDefined<QA>((a, e) => a.Question == "" && e.Question == "" ? 0.7 : 0.0)
            .Score(new QA { Question = "", Answer = "x" }, new QA { Question = "", Answer = "y" }, Meta)
            .ShouldBe(0.7);
}

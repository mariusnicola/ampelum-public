using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class LabeledExampleTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public void Construct_CarriesInputsAndExpected()
    {
        var inputs  = new QA { Question = "cap?" };
        var exp     = new QA { Question = "cap?", Answer = "Paris" };
        var le      = new LabeledExample<QA>(inputs, exp);
        le.Inputs.ShouldBe(inputs);
        le.Expected.ShouldBe(exp);
    }

    [Fact]
    public void Record_SupportsValueEquality()
    {
        var a1 = new LabeledExample<QA>(new QA { Question = "x" }, new QA { Question = "x", Answer = "y" });
        var a2 = new LabeledExample<QA>(new QA { Question = "x" }, new QA { Question = "x", Answer = "y" });
        a1.ShouldBe(a2);
    }
}

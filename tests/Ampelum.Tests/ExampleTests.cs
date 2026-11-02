using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class ExampleTests
{
    [Signature("")]
    sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public void Ctor_StoresDictionaries()
    {
        var ins = new Dictionary<string, object?> { ["q"] = 1 };
        var outs = new Dictionary<string, object?> { ["a"] = 2 };
        var ex = new Example(ins, outs);
        ex.Inputs["q"].ShouldBe(1);
        ex.Outputs["a"].ShouldBe(2);
    }

    [Fact]
    public void From_ProjectsInputsAndOutputs()
    {
        var src = new Qa { Question = "?", Answer = "!" };
        var ex = Example.From(src);
        ex.Inputs.Keys.ShouldBe(["Question"], ignoreOrder: true);
        ex.Outputs.Keys.ShouldBe(["Answer"], ignoreOrder: true);
        ex.Inputs["Question"].ShouldBe("?");
        ex.Outputs["Answer"].ShouldBe("!");
    }

    [Fact]
    public void From_NullArg_Throws() =>
        Should.Throw<ArgumentNullException>(() => Example.From<Qa>(null!));
}

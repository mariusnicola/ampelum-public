using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

public sealed class SignatureParseTests
{
    [Fact]
    public void Parse_SingleInputSingleOutput()
    {
        var meta = Signature.Parse("question -> answer");
        meta.IsDynamic.ShouldBeTrue();
        meta.SignatureType.ShouldBe(typeof(Dictionary<string, object?>));
        meta.Inputs.Select(f => f.Name).ShouldBe(["question"]);
        meta.Outputs.Select(f => f.Name).ShouldBe(["answer"]);
        meta.Inputs[0].ClrType.ShouldBe(typeof(string));
        meta.Instructions.ShouldBe("");
    }

    [Fact]
    public void Parse_MultipleFieldsPerSide_TrimsWhitespace()
    {
        var meta = Signature.Parse("  a ,b -> c , d ");
        meta.Inputs.Select(f => f.Name).ShouldBe(["a", "b"]);
        meta.Outputs.Select(f => f.Name).ShouldBe(["c", "d"]);
    }

    [Fact]
    public void Parse_InstructionsViaParameter()
    {
        var meta = Signature.Parse("q -> a", instructions: "Answer short.");
        meta.Instructions.ShouldBe("Answer short.");
    }

    [Fact]
    public void Parse_InstructionsViaInlineDoubleSlash()
    {
        var meta = Signature.Parse("Summarize docs. // doc -> summary");
        meta.Instructions.ShouldBe("Summarize docs.");
        meta.Inputs[0].Name.ShouldBe("doc");
        meta.Outputs[0].Name.ShouldBe("summary");
    }

    [Fact]
    public void Parse_InlineInstructions_OverrideParameter()
    {
        var meta = Signature.Parse("Inline. // x -> y", instructions: "Param.");
        meta.Instructions.ShouldBe("Inline.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no arrow here")]
    [InlineData("a -> b -> c")]
    [InlineData("-> b")]
    [InlineData("a ->")]
    [InlineData(", -> b")]
    [InlineData("a -> ,")]
    public void Parse_InvalidSyntax_Throws(string dsl) =>
        Should.Throw<SignatureDefinitionException>(() => Signature.Parse(dsl));

    [Fact]
    public void Parse_NullDsl_Throws() =>
        Should.Throw<ArgumentNullException>(() => Signature.Parse(null!));
}

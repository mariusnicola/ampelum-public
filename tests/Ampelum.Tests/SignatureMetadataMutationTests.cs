using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class SignatureMetadataMutationTests
{
    static SignatureMetadata MakeMeta() => new(
        SignatureType: typeof(object),
        Instructions: "original instructions",
        Inputs:  new List<Field> { new("question", typeof(string), "The question", FieldKind.Input,  IsOptional: false) },
        Outputs: new List<Field> { new("answer",   typeof(string), "The answer",   FieldKind.Output, IsOptional: false) });

    [Fact]
    public void WithInstructions_ReturnsNewInstanceWithUpdatedInstructions()
    {
        var original = MakeMeta();
        var modified = original.WithInstructions("new instructions");
        modified.Instructions.ShouldBe("new instructions");
        modified.SignatureType.ShouldBe(original.SignatureType);
        modified.Inputs.ShouldBe(original.Inputs);
        modified.Outputs.ShouldBe(original.Outputs);
    }

    [Fact]
    public void WithInstructions_DoesNotMutateOriginal()
    {
        var original = MakeMeta();
        _ = original.WithInstructions("new instructions");
        original.Instructions.ShouldBe("original instructions");
    }

    [Fact]
    public void WithInstructions_ReturnsDistinctInstance()
    {
        var original = MakeMeta();
        var modified = original.WithInstructions("x");
        ReferenceEquals(original, modified).ShouldBeFalse();
    }

    [Fact]
    public void WithUpdatedFields_UpdatesMatchingFieldDescriptions()
    {
        var original = MakeMeta();
        var descs = new Dictionary<string, string>
        {
            ["question"] = "Updated question desc",
            ["answer"]   = "Updated answer desc"
        };
        var modified = original.WithUpdatedFields(descs);
        modified.Inputs[0].Description.ShouldBe("Updated question desc");
        modified.Outputs[0].Description.ShouldBe("Updated answer desc");
    }

    [Fact]
    public void WithUpdatedFields_LeavesUnmatchedFieldsUnchanged()
    {
        var original = MakeMeta();
        var modified = original.WithUpdatedFields(new Dictionary<string, string> { ["answer"] = "new" });
        modified.Inputs[0].Description.ShouldBe("The question");
        modified.Outputs[0].Description.ShouldBe("new");
    }

    [Fact]
    public void WithUpdatedFields_DoesNotMutateOriginal()
    {
        var original = MakeMeta();
        _ = original.WithUpdatedFields(new Dictionary<string, string> { ["question"] = "new" });
        original.Inputs[0].Description.ShouldBe("The question");
    }

    [Fact]
    public void WithUpdatedFields_EmptyDictionary_ReturnsEquivalentInstance()
    {
        var original = MakeMeta();
        var modified = original.WithUpdatedFields(new Dictionary<string, string>());
        modified.Instructions.ShouldBe(original.Instructions);
        modified.Inputs[0].Description.ShouldBe(original.Inputs[0].Description);
        modified.Outputs[0].Description.ShouldBe(original.Outputs[0].Description);
    }
}

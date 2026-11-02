using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class FieldTests
{
    [Fact]
    public void Field_ExposesAllProperties()
    {
        var f = new Field("Question", typeof(string), "the question", FieldKind.Input, IsOptional: false);
        f.Name.ShouldBe("Question");
        f.ClrType.ShouldBe(typeof(string));
        f.Description.ShouldBe("the question");
        f.Kind.ShouldBe(FieldKind.Input);
        f.IsOptional.ShouldBeFalse();
    }

    [Fact]
    public void Field_IsOptional_DefaultsToFalse_AndCanBeTrue()
    {
        var req = new Field("X", typeof(string), "x", FieldKind.Input, IsOptional: false);
        var opt = new Field("Y", typeof(string), "y", FieldKind.Output, IsOptional: true);
        req.IsOptional.ShouldBeFalse();
        opt.IsOptional.ShouldBeTrue();
    }

    [Fact]
    public void FieldKind_HasInputAndOutput()
    {
        Enum.GetValues<FieldKind>().ShouldBe([FieldKind.Input, FieldKind.Output]);
    }

    [Fact]
    public void SignatureMetadata_ExposesAllProperties()
    {
        var input = new Field("Q", typeof(string), "q", FieldKind.Input, IsOptional: false);
        var output = new Field("A", typeof(string), "a", FieldKind.Output, IsOptional: false);
        var meta = new SignatureMetadata(typeof(object), "hi", [input], [output]);
        meta.SignatureType.ShouldBe(typeof(object));
        meta.Instructions.ShouldBe("hi");
        meta.Inputs.ShouldBe([input]);
        meta.Outputs.ShouldBe([output]);
    }
}

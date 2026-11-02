using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class AttributesTests
{
    [Fact]
    public void SignatureAttribute_StoresInstructions()
    {
        var attr = new SignatureAttribute("hello");
        attr.Instructions.ShouldBe("hello");
    }

    [Fact]
    public void InputAttribute_DescIsNullByDefault()
    {
        new InputAttribute().Desc.ShouldBeNull();
    }

    [Fact]
    public void InputAttribute_StoresDesc()
    {
        new InputAttribute { Desc = "x" }.Desc.ShouldBe("x");
    }

    [Fact]
    public void OutputAttribute_DescIsNullByDefault()
    {
        new OutputAttribute().Desc.ShouldBeNull();
    }

    [Fact]
    public void OutputAttribute_StoresDesc()
    {
        new OutputAttribute { Desc = "y" }.Desc.ShouldBe("y");
    }
}

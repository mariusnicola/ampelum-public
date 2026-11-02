using SignalGrid.Ampelum.Tools;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Tools;

public sealed class ToolCategoryAttributeTests
{
    [ToolCategory(ToolCategoryKind.Read)] sealed class T1 : ITypedTool<int>
    { public string Name => "t1"; public string Description => ""; public ValueTask<int> InvokeAsync(CancellationToken ct) => default; }

    [Fact]
    public void Attribute_CarriesKindAndIsDiscoverable()
    {
        var attr = typeof(T1).GetCustomAttributes(typeof(ToolCategoryAttribute), inherit: false)
            .Cast<ToolCategoryAttribute>().Single();
        attr.Kind.ShouldBe(ToolCategoryKind.Read);
    }

    [Fact]
    public void Enum_HasFourValues()
    {
        var values = Enum.GetValues<ToolCategoryKind>();
        values.ShouldBe(new[] { ToolCategoryKind.Read, ToolCategoryKind.Write, ToolCategoryKind.Compute, ToolCategoryKind.External });
    }

    [Fact]
    public void Attribute_TargetsClass()
    {
        var u = typeof(ToolCategoryAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>().Single();
        u.ValidOn.HasFlag(AttributeTargets.Class).ShouldBeTrue();
    }
}

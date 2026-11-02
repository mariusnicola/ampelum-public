using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests;

public sealed class GenerateSignatureMetadataAttributeTests
{
    [Fact]
    public void Attribute_HasAssemblyTarget()
    {
        var usage = typeof(GenerateSignatureMetadataAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        usage.ValidOn.ShouldBe(AttributeTargets.Assembly);
        usage.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public void Attribute_CanBeInstantiated()
    {
        var attr = new GenerateSignatureMetadataAttribute();
        attr.ShouldNotBeNull();
    }
}

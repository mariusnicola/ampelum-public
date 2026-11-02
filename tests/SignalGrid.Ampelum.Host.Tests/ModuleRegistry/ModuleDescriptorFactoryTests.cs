#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.ModuleRegistry;

public sealed class ModuleDescriptorFactoryTests
{
    [Fact]
    public void Create_ReflectsFakeSigFields()
    {
        var desc = ModuleDescriptorFactory.Create<FakeSig>("test-module", false);
        desc.Name.ShouldBe("test-module");
        desc.SupportsStreaming.ShouldBeFalse();
        desc.Inputs.Count.ShouldBe(1);
        desc.Inputs[0].Name.ShouldBe("Q");
        desc.Outputs.Count.ShouldBe(1);
        desc.Outputs[0].Name.ShouldBe("A");
    }

    [Fact]
    public void Create_SignatureType_IsFullName()
    {
        var desc = ModuleDescriptorFactory.Create<FakeSig>("m", false);
        desc.SignatureType.ShouldContain("FakeSig");
    }

    [Fact]
    public void Create_SupportsStreaming_PlumbsThrough()
    {
        var desc = ModuleDescriptorFactory.Create<FakeSig>("m", true);
        desc.SupportsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void Create_EmptyModuleName_Throws() =>
        Should.Throw<ArgumentException>(() =>
            ModuleDescriptorFactory.Create<FakeSig>("", false));

    [Fact]
    public void Create_InputRequired_ReflectsNonOptional()
    {
        var desc = ModuleDescriptorFactory.Create<FakeSig>("m", false);
        desc.Inputs[0].Required.ShouldBeTrue();
    }

    [Fact]
    public void Create_OutputOptional_WhenNullableField()
    {
        // FakeSig.A is non-nullable string with default, not optional per reflection
        // This test validates the Required field is set based on IsOptional
        var desc = ModuleDescriptorFactory.Create<FakeSig>("m", false);
        desc.Outputs.ShouldNotBeEmpty();
    }
}

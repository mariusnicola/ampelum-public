using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.BuildTimeTools.Tests;

// These tests run against the sample assembly, which has the generator attached.
public sealed class GeneratedCodeTests
{
    [Fact]
    public void MetadataInitializer_IsCompiledIntoSampleAssembly()
    {
        var initializer = typeof(Summarize).Assembly
            .GetType("SignalGrid.Ampelum.Generated.Summarize_MetadataInitializer");

        initializer.ShouldNotBeNull();
    }

    [Fact]
    public void AccessorRegistry_IsCompiledIntoSampleAssembly()
    {
        var registry = typeof(Summarize).Assembly
            .GetType("SignalGrid.Ampelum.Generated.__AmpelumSignatureAccessors");

        registry.ShouldNotBeNull();
    }

    [Fact]
    public void GeneratedAccessor_ReadsTheInputProperty()
    {
        SignatureMetadataCache.TryGetAccessors(typeof(Summarize), out var accessors).ShouldBeTrue();
        accessors.ShouldNotBeNull();
        accessors.Length.ShouldBe(1);

        var read = ((Func<Summarize, object?>)accessors[0])(new Summarize { Passage = "abc" });

        read.ShouldBe("abc");
    }

    // Reflection check, not generator proof: the reflection path builds the same metadata
    // when the generator is detached, so this test passes either way.
    [Fact]
    public void SignatureMetadata_MatchesDeclaredNamesAndDescriptions_ReflectionCheck()
    {
        var metadata = SignatureMetadataCache.For<Summarize>();

        metadata.Instructions.ShouldBe("Summarize the passage in one sentence.");
        metadata.Inputs.Select(f => f.Name).ShouldBe(["Passage"]);
        metadata.Inputs[0].Description.ShouldBe("the text to summarize");
        metadata.Outputs.Select(f => f.Name).ShouldBe(["Summary"]);
        metadata.Outputs[0].Description.ShouldBe("one sentence");
    }
}

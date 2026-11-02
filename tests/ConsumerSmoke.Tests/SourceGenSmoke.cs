using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace ConsumerSmoke.Tests;

/// <summary>Signature declared exclusively in this consumer assembly.
/// Validates that the Ampelum.SourceGenerator nupkg ships with the analyzer/
/// generator bits the consumer's compilation needs to emit a metadata
/// initializer for this type at build time.</summary>
[Signature("Translate the input phrase to French.")]
public sealed record ConsumerTranslateSig : ISignature
{
    /// <summary>English phrase.</summary>
    [Input] public string English { get; init; } = "";
    /// <summary>French translation.</summary>
    [Output] public string French { get; init; } = "";
}

/// <summary>
/// Scenario B — source-generated metadata for a consumer-declared signature.
/// If the source generator did NOT run inside the consumer compilation,
/// <see cref="SignatureMetadataCache.For{TSig}"/> throws because no
/// <c>[ModuleInitializer]</c> registered the entry. The lookup succeeding is
/// the smoke signal.
/// </summary>
public sealed class SourceGenSmoke
{
    [Fact]
    public void For_ReturnsGeneratedMetadata_ForConsumerSignature()
    {
        var ct = TestContext.Current.CancellationToken;
        ct.ThrowIfCancellationRequested();

        var meta = SignatureMetadataCache.For<ConsumerTranslateSig>();

        meta.ShouldNotBeNull();
        meta.Instructions.ShouldBe("Translate the input phrase to French.");
        meta.Inputs.Count.ShouldBe(1);
        meta.Inputs[0].Name.ShouldBe("English");
        meta.Outputs.Count.ShouldBe(1);
        meta.Outputs[0].Name.ShouldBe("French");
    }
}

using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class SignatureMetadataIsDynamicTests
{
    [Signature("Typed.")]
    public sealed record Typed : ISignature
    {
        [Input]  public required string In { get; init; }
        [Output] public string Out { get; init; } = "";
    }

    [Fact]
    public void IsDynamic_False_ForTypedRecord() =>
        SignatureMetadataCache.For<Typed>().IsDynamic.ShouldBeFalse();

    [Fact]
    public void IsDynamic_True_WhenSignatureTypeIsDictionarySentinel()
    {
        var meta = new SignatureMetadata(
            typeof(Dictionary<string, object?>),
            "",
            [new Field("a", typeof(string), "", FieldKind.Input, IsOptional: false)],
            [new Field("b", typeof(string), "", FieldKind.Output, IsOptional: false)]);
        meta.IsDynamic.ShouldBeTrue();
    }
}

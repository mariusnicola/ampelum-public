using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class SignatureMetadataCacheRegisterTests
{
    [Signature("")] sealed record R1 : ISignature { [Input] public required string X { get; init; } [Output] public string Y { get; init; } = ""; }
    [Signature("")] sealed record R2 : ISignature { [Input] public required string X { get; init; } [Output] public string Y { get; init; } = ""; }

    [Fact]
    public void Register_StoresMetadata_ForUnknownType()
    {
        var t = typeof(R1);
        var meta = new SignatureMetadata(t, "override", [], []);
        SignatureMetadataCache.Register(t, meta);
        SignatureMetadataCache.For(t).Instructions.ShouldBe("override");
    }

    [Fact]
    public void Register_IsNoOp_WhenForAlreadyRan()
    {
        var t = typeof(R2);
        var reflected = SignatureMetadataCache.For(t);   // forces reflection build
        SignatureMetadataCache.Register(t, new SignatureMetadata(t, "override", [], []));
        SignatureMetadataCache.For(t).ShouldBeSameAs(reflected);
    }

    [Fact] public void Register_NullType_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            SignatureMetadataCache.Register(null!, new SignatureMetadata(typeof(R1), "", [], [])));

    [Fact] public void Register_NullMetadata_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            SignatureMetadataCache.Register(typeof(R1), null!));
}

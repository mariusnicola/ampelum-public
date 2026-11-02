using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp015Tests
{
    [Fact]
    public async Task NonSignatureType_NoDiagnostic()
    {
        var src = """
        public sealed class C { public string Name => "test"; }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp015SignatureRequiredAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP015").ShouldBeEmpty();
    }

    [Fact]
    public async Task AbstractSignatureType_NoDiagnostic()
    {
        var src = """
        using SignalGrid.Ampelum;
        public abstract class AbstractSig : ISignature { }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp015SignatureRequiredAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP015").ShouldBeEmpty();
    }

    [Fact]
    public async Task SignatureTypeWithAttribute_NoDiagnostic()
    {
        var src = """
        using SignalGrid.Ampelum;
        [Signature] public sealed class GoodSig : ISignature { }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp015SignatureRequiredAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP015").ShouldBeEmpty();
    }

    [Fact]
    public async Task SignatureTypeMissingAttribute_TriggersAmp015()
    {
        var src = """
        using SignalGrid.Ampelum;
        public sealed class BadSig : ISignature { }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp015SignatureRequiredAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP015").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("BadSig");
    }
}

using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp013Tests
{
    [Fact]
    public async Task NoDynamic_NoDiagnostic()
    {
        var src = """
        public sealed class C
        {
            public string? Value { get; set; }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp013NoDynamicAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP013").ShouldBeEmpty();
    }

    [Fact]
    public async Task DynamicProperty_TriggersAmp013()
    {
        var src = """
        public sealed class C
        {
            public dynamic Value { get; set; }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp013NoDynamicAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP013").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("Value");
    }

    [Fact]
    public async Task DynamicParameter_TriggersAmp013()
    {
        var src = """
        public sealed class C
        {
            public void Execute(dynamic input) { }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp013NoDynamicAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP013").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("Execute");
    }

    [Fact]
    public async Task VariableNamedDynamic_NoDiagnostic()
    {
        var src = """
        public sealed class C
        {
            public void M() { var dynamic = 5; }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp013NoDynamicAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP013").ShouldBeEmpty();
    }
}

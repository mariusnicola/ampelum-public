using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp011Tests
{
    [Fact]
    public async Task FewPublicMethods_NoDiagnostic()
    {
        var src = """
        public sealed class C
        {
            public void A() { } public void B() { } public void C() { }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp011MaxPublicMethodsAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP011").ShouldBeEmpty();
    }

    [Fact]
    public async Task ManyPublicMethods_TriggersAmp011()
    {
        var methods = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"    public void M{i}() {{ }}"));
        var src = $"public sealed class Overloaded\n{{\n{methods}\n}}";
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp011MaxPublicMethodsAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP011").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("Overloaded");
    }

    [Fact]
    public async Task NonPublicMethodsNotCounted()
    {
        var src = """
        public sealed class C
        {
            private void A() { } internal void B() { } protected void C() { }
            public void D() { }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp011MaxPublicMethodsAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP011").ShouldBeEmpty();
    }

    [Fact]
    public async Task Struct_WithManyMethods_TriggersAmp011()
    {
        var methods = string.Join("\n", Enumerable.Range(0, 15).Select(i => $"    public void M{i}() {{ }}"));
        var src = $"public struct StructOverloaded\n{{\n{methods}\n}}";
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp011MaxPublicMethodsAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP011").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("StructOverloaded");
    }
}

using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp010Tests
{
    [Fact]
    public async Task ShortFile_NoDiagnostic()
    {
        var src = """
        public sealed class ShortFile
        {
            public string Name => "test";
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp010MaxFileLinesAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP010").ShouldBeEmpty();
    }

    [Fact]
    public async Task ExceedsDefaultMaxLines_TriggersAmp010()
    {
        var src = string.Join("\n", Enumerable.Range(0, 410).Select(i => i == 0 ? "public sealed class C { }" : "// line " + i));
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp010MaxFileLinesAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP010").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("410");
    }

    [Fact]
    public async Task Exactly400Lines_NoDiagnostic()
    {
        var src = string.Join("\n", Enumerable.Range(0, 400).Select(i => "// line " + i));
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp010MaxFileLinesAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP010").ShouldBeEmpty();
    }
}

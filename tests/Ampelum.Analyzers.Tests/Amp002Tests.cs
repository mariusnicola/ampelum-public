using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp002Tests
{
    [Fact]
    public async Task TypedTool_WithToolCategory_NoDiagnostic()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        [ToolCategory(ToolCategoryKind.Read)]
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp002ToolCategoryAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP002").ShouldBeEmpty();
    }

    [Fact]
    public async Task TypedTool_MissingToolCategory_TriggersAmp002()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp002ToolCategoryAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP002").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("'T'");
    }

    [Fact]
    public async Task NonTool_NotFlagged()
    {
        var src = "public sealed class X { }";
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp002ToolCategoryAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP002").ShouldBeEmpty();
    }
}

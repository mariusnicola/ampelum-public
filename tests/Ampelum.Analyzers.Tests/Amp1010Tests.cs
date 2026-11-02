using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp1010Tests
{
    [Fact]
    public async Task FreeForm_WithoutAssemblyOptIn_TriggersAmp1010()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            [FreeForm] public string Q { get; set; } = "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp1010FreeFormOptInAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP1010").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("'Q'");
    }

    [Fact]
    public async Task FreeForm_WithAssemblyOptIn_NoDiagnostic()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        [assembly: AllowFreeForm]
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            [FreeForm] public string Q { get; set; } = "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp1010FreeFormOptInAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP1010").ShouldBeEmpty();
    }

    [Fact]
    public async Task NoFreeForm_NoDiagnostic()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            public int N { get; set; }
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp1010FreeFormOptInAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP1010").ShouldBeEmpty();
    }

    [Fact]
    public async Task FreeForm_OnParameter_AlsoFlagged()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class T
        {
            public void Do([FreeForm] string raw) { _ = raw; }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp1010FreeFormOptInAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP1010").Count().ShouldBe(1);
    }
}

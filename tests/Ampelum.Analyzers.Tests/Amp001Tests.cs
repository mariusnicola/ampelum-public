using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp001Tests
{
    [Fact]
    public async Task FreeFormStringProperty_NoDiagnostic()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            [FreeForm] public string? Value { get; init; }
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp001FreeFormAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP001").ShouldBeEmpty();
    }

    [Fact]
    public async Task PlainStringProperty_TriggersAmp001()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            public string Value { get; init; } = "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp001FreeFormAnalyzer(), src, TestContext.Current.CancellationToken);
        var hits = diags.Where(d => d.Id == "AMP001").ToArray();
        hits.Length.ShouldBe(1);
        hits[0].GetMessage().ShouldContain("Value");
    }

    [Fact]
    public async Task NameAndDescription_AreNotFlagged()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class T : ITypedTool<int>
        {
            public string Name => "t"; public string Description => "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp001FreeFormAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP001").ShouldBeEmpty();
    }

    [Fact]
    public async Task NonTypedToolType_NotFlagged()
    {
        var src = """
        public sealed class NotATool { public string V { get; set; } = ""; }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp001FreeFormAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP001").ShouldBeEmpty();
    }
}

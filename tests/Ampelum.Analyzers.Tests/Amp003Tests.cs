using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp003Tests
{
    [Fact]
    public async Task SendNamed_WithoutPreviewable_TriggersAmp003()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class SendEmailTool : ITypedTool<int>
        {
            public string Name => "send"; public string Description => "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp003SideEffectPreviewableAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP003").Count().ShouldBe(1);
    }

    [Fact]
    public async Task SendNamed_WithPreviewable_NoDiagnostic()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class SendEmailTool : ITypedTool<int>, IPreviewable
        {
            public string Name => "send"; public string Description => "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
            public string PreviewAsync(System.Threading.CancellationToken ct) => "preview";
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp003SideEffectPreviewableAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP003").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DeleteUserTool")]
    [InlineData("WriteFileTool")]
    [InlineData("UpdateRecordTool")]
    [InlineData("InsertRowTool")]
    [InlineData("DropTableTool")]
    [InlineData("ExecScriptTool")]
    public async Task AllSideEffectPrefixes_TriggerWhenNotPreviewable(string typeName)
    {
        var src = $$"""
        using SignalGrid.Ampelum.Tools;
        public sealed class {{typeName}} : ITypedTool<int>
        {
            public string Name => "n"; public string Description => "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp003SideEffectPreviewableAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP003").Count().ShouldBe(1);
    }

    [Fact]
    public async Task ReadOnlyTool_NotFlagged()
    {
        var src = """
        using SignalGrid.Ampelum.Tools;
        public sealed class GetUserTool : ITypedTool<int>
        {
            public string Name => "n"; public string Description => "";
            public System.Threading.Tasks.ValueTask<int> InvokeAsync(System.Threading.CancellationToken ct) => default;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp003SideEffectPreviewableAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP003").ShouldBeEmpty();
    }
}

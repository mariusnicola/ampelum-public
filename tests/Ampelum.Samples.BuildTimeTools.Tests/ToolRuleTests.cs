using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SignalGrid.Ampelum.Analyzers;
using Ampelum.Samples.BuildTimeTools.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.BuildTimeTools.Tests;

// Rules about ITypedTool implementations: AMP001, AMP002, AMP003, AMP1010.
public sealed class ToolRuleTests
{
    const string ToolHeader = """
        using System.Threading;
        using System.Threading.Tasks;
        using SignalGrid.Ampelum.Tools;
        """;

    [Fact]
    public async Task Amp001_PlainStringProperty_Fires()
    {
        var source = ToolHeader + """

        [ToolCategory(ToolCategoryKind.Read)]
        public sealed class LookupTool : ITypedTool<int>
        {
            public string Name => "lookup";
            public string Description => "lookup";
            public string Query { get; init; } = "";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
        }
        """;

        var hit = (await Hits(new Amp001FreeFormAnalyzer(), source, "AMP001")).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("Query", 9, 19));
        hit.GetMessage().ShouldBe("Property 'Query' is a string on ITypedTool 'LookupTool' and is missing [FreeForm]");
    }

    [Fact]
    public async Task Amp001_FreeFormStringProperty_IsQuiet()
    {
        var source = ToolHeader + """

        [ToolCategory(ToolCategoryKind.Read)]
        public sealed class LookupTool : ITypedTool<int>
        {
            public string Name => "lookup";
            public string Description => "lookup";
            [FreeForm] public string Query { get; init; } = "";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
        }
        """;

        (await Hits(new Amp001FreeFormAnalyzer(), source, "AMP001")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp002_ToolWithoutCategory_Fires()
    {
        var source = ToolHeader + """

        public sealed class LookupTool : ITypedTool<int>
        {
            public string Name => "lookup";
            public string Description => "lookup";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
        }
        """;

        var hit = (await Hits(new Amp002ToolCategoryAnalyzer(), source, "AMP002")).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("LookupTool", 4, 21));
        hit.GetMessage().ShouldBe("Type 'LookupTool' implements ITypedTool but lacks [ToolCategory]");
    }

    [Fact]
    public async Task Amp002_ToolWithCategory_IsQuiet()
    {
        var source = ToolHeader + """

        [ToolCategory(ToolCategoryKind.Read)]
        public sealed class LookupTool : ITypedTool<int>
        {
            public string Name => "lookup";
            public string Description => "lookup";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
        }
        """;

        (await Hits(new Amp002ToolCategoryAnalyzer(), source, "AMP002")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp003_SideEffectToolWithoutPreview_Fires()
    {
        var source = ToolHeader + """

        [ToolCategory(ToolCategoryKind.Write)]
        public sealed class SendInvoiceTool : ITypedTool<int>
        {
            public string Name => "send-invoice";
            public string Description => "send";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
        }
        """;

        var hit = (await Hits(new Amp003SideEffectPreviewableAnalyzer(), source, "AMP003")).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("SendInvoiceTool", 5, 21));
        hit.GetMessage().ShouldBe("Type 'SendInvoiceTool' looks side-effecting (prefix 'Send') but does not implement IPreviewable");
    }

    [Fact]
    public async Task Amp003_SideEffectToolWithPreview_IsQuiet()
    {
        var source = ToolHeader + """

        [ToolCategory(ToolCategoryKind.Write)]
        public sealed class SendInvoiceTool : ITypedTool<int>, IPreviewable
        {
            public string Name => "send-invoice";
            public string Description => "send";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
            public string PreviewAsync(CancellationToken ct) => "would send one invoice";
        }
        """;

        (await Hits(new Amp003SideEffectPreviewableAnalyzer(), source, "AMP003")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp1010_FreeFormWithoutAssemblyOptIn_Fires()
    {
        var source = ToolHeader + """

        [ToolCategory(ToolCategoryKind.Read)]
        public sealed class LookupTool : ITypedTool<int>
        {
            public string Name => "lookup";
            public string Description => "lookup";
            [FreeForm] public string Query { get; init; } = "";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
        }
        """;

        var hit = (await Hits(new Amp1010FreeFormOptInAnalyzer(), source, "AMP1010")).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("Query", 9, 30));
        hit.GetMessage().ShouldBe("'Query' is marked [FreeForm] but the assembly is missing [assembly: Ampelum.Tools.AllowFreeForm]");
    }

    [Fact]
    public async Task Amp1010_FreeFormWithAssemblyOptIn_IsQuiet()
    {
        var source = ToolHeader + """

        [assembly: AllowFreeForm]

        [ToolCategory(ToolCategoryKind.Read)]
        public sealed class LookupTool : ITypedTool<int>
        {
            public string Name => "lookup";
            public string Description => "lookup";
            [FreeForm] public string Query { get; init; } = "";
            public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(0);
        }
        """;

        (await Hits(new Amp1010FreeFormOptInAnalyzer(), source, "AMP1010")).ShouldBeEmpty();
    }

    static async Task<ImmutableArray<Diagnostic>> Hits(DiagnosticAnalyzer analyzer, string source, string id)
    {
        var all = await AnalyzerVerifier.RunAsync(analyzer, source, TestContext.Current.CancellationToken);
        return all.Where(d => d.Id == id).ToImmutableArray();
    }
}

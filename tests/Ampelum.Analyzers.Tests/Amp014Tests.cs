using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp014Tests
{
    [Fact]
    public async Task OrdinaryCode_NoDiagnostic()
    {
        var src = """
        public sealed class C
        {
            public string Format(string s) => s.ToUpper();
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp014NoBareLlmPromptsAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP014").ShouldBeEmpty();
    }

    [Fact]
    public async Task NoExternalApiCalls_NoDiagnostic()
    {
        var src = """
        public sealed class C
        {
            public int Sum(int a, int b) => a + b;
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp014NoBareLlmPromptsAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP014").ShouldBeEmpty();
    }

    [Fact]
    public async Task PlainMethodCall_NoDiagnostic()
    {
        var src = """
        public sealed class C
        {
            public string Work(string s) => s.ToUpper();
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp014NoBareLlmPromptsAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP014").ShouldBeEmpty();
    }
}

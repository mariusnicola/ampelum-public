using SignalGrid.Ampelum.Analyzers;
using SignalGrid.Ampelum.Analyzers.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class Amp012Tests
{
    [Fact]
    public async Task SameLayerTypes_NoDiagnostic()
    {
        var src = """
        namespace MyApp.Controllers;
        public class HomeController
        {
            public Services.UserService? UserService { get; set; }
        }
        namespace MyApp.Services;
        public class UserService { }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp012LayerRuleAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP012").ShouldBeEmpty();
    }

    [Fact]
    public async Task ControllersService_SameLayer_NoDiagnostic()
    {
        var src = """
        namespace MyApp.Controllers;
        public class HomeController
        {
            public void Index() { }
        }
        namespace MyApp.Controllers;
        public class AboutController
        {
            public void Info() { }
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp012LayerRuleAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP012").ShouldBeEmpty();
    }

    [Fact]
    public async Task NonLayerNamespace_NoDiagnostic()
    {
        var src = """
        namespace MyApp.Utils;
        public class Helper
        {
            public string Format(string s) => s.ToUpper();
        }
        """;
        var diags = await CSharpAnalyzerVerifier.RunAsync(new Amp012LayerRuleAnalyzer(), src, TestContext.Current.CancellationToken);
        diags.Where(d => d.Id == "AMP012").ShouldBeEmpty();
    }
}

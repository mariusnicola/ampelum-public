using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SignalGrid.Ampelum.Analyzers;
using Ampelum.Samples.BuildTimeTools.Tests.Verifiers;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.BuildTimeTools.Tests;

// Size and layering rules: AMP010, AMP011, AMP012. Each reads an .editorconfig key
// when one is given and falls back to its built-in limit otherwise.
public sealed class ShapeRuleTests
{
    const string LayeredSource = """
        namespace Shop.Controllers
        {
            public sealed class OrderController
            {
                public Shop.Services.OrderService? Service { get; set; }
            }
        }

        namespace Shop.Services
        {
            public sealed class OrderService { }
        }
        """;

    [Fact]
    public async Task Amp010_FileOverDefaultLimit_Fires()
    {
        var hit = (await Hits(new Amp010MaxFileLinesAnalyzer(), FileWithLines(401), "AMP010", null)).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("}", 401, 30));
        hit.GetMessage().ShouldBe("File has 401 lines, which exceeds the maximum of 400");
    }

    [Fact]
    public async Task Amp010_FileAtDefaultLimit_IsQuiet()
    {
        (await Hits(new Amp010MaxFileLinesAnalyzer(), FileWithLines(400), "AMP010", null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp010_ConfiguredLimit_IsRead()
    {
        var config = Config("dotnet_diagnostic.AMP010.max_file_lines", "5");

        var hit = (await Hits(new Amp010MaxFileLinesAnalyzer(), FileWithLines(6), "AMP010", config)).Single();
        hit.GetMessage().ShouldBe("File has 6 lines, which exceeds the maximum of 5");
        (await Hits(new Amp010MaxFileLinesAnalyzer(), FileWithLines(5), "AMP010", config)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp011_TypeOverDefaultLimit_Fires()
    {
        var hit = (await Hits(new Amp011MaxPublicMethodsAnalyzer(), TypeWithMethods(11), "AMP011", null)).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("Wide", 1, 21));
        hit.GetMessage().ShouldBe("Type 'Wide' has 11 public methods, which exceeds the maximum of 10");
    }

    [Fact]
    public async Task Amp011_TypeAtDefaultLimit_IsQuiet()
    {
        (await Hits(new Amp011MaxPublicMethodsAnalyzer(), TypeWithMethods(10), "AMP011", null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp011_ConfiguredLimit_IsRead()
    {
        var config = Config("dotnet_diagnostic.AMP011.max_public_methods", "2");

        var hit = (await Hits(new Amp011MaxPublicMethodsAnalyzer(), TypeWithMethods(3), "AMP011", config)).Single();
        hit.GetMessage().ShouldBe("Type 'Wide' has 3 public methods, which exceeds the maximum of 2");
        (await Hits(new Amp011MaxPublicMethodsAnalyzer(), TypeWithMethods(2), "AMP011", config)).ShouldBeEmpty();
    }

    // Known analyzer bug, recorded here so it stays visible. Under the default order
    // Controllers;Services;DataAccess the rule flags a controller calling a service,
    // which is the opposite of normal layering. This is not a working feature.
    [Fact]
    public async Task Amp012_KnownInversion_ControllerCallingServiceIsFlaggedUnderDefaultOrder()
    {
        var hit = (await Hits(new Amp012LayerRuleAnalyzer(), LayeredSource, "AMP012", null)).Single();

        AnalyzerVerifier.Where(hit).ShouldBe(("OrderController", 3, 25));
        hit.GetMessage().ShouldStartWith("'OrderController' (in layer 'Controllers') imports 'OrderService' (from higher layer 'Services')");
    }

    [Fact]
    public async Task Amp012_SameLayerReference_IsQuiet()
    {
        const string source = """
            namespace Shop.Controllers
            {
                public sealed class OrderController
                {
                    public OrderFilter? Filter { get; set; }
                }

                public sealed class OrderFilter { }
            }
            """;

        (await Hits(new Amp012LayerRuleAnalyzer(), source, "AMP012", null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Amp012_ConfiguredLayerOrder_IsRead()
    {
        var config = Config("dotnet_diagnostic.AMP012.layer_order", "Services;Controllers");

        (await Hits(new Amp012LayerRuleAnalyzer(), LayeredSource, "AMP012", config)).ShouldBeEmpty();
    }

    static string FileWithLines(int count) =>
        string.Join("\n", Enumerable.Repeat("// filler", count - 1).Append("public static class Filler { }"));

    static string TypeWithMethods(int count)
    {
        var methods = Enumerable.Range(0, count).Select(i => $"    public void M{i}() {{ }}");
        return "public sealed class Wide\n{\n" + string.Join("\n", methods) + "\n}\n";
    }

    static IReadOnlyDictionary<string, string> Config(string key, string value) =>
        new Dictionary<string, string> { [key] = value };

    static async Task<ImmutableArray<Diagnostic>> Hits(
        DiagnosticAnalyzer analyzer, string source, string id, IReadOnlyDictionary<string, string>? config)
    {
        var all = await AnalyzerVerifier.RunAsync(analyzer, source, TestContext.Current.CancellationToken, config);
        return all.Where(d => d.Id == id).ToImmutableArray();
    }
}

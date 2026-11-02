using System.IO.Compression;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Shouldly;
using Xunit;

namespace ConsumerSmoke.Tests;

/// <summary>
/// Scenario C — analyzer pack ships and fires.
/// Reads <c>Ampelum.Analyzers.0.8.0-rc1.nupkg</c> directly from <c>_packs/</c>,
/// asserts the published nupkg contains <c>analyzers/dotnet/cs/Ampelum.Analyzers.dll</c>
/// (the path NuGet looks for to wire analyzers into a consumer build), then loads
/// that DLL in-process and runs <c>Amp1010FreeFormOptInAnalyzer</c> against a
/// snippet that violates POLC. AMP1010 must surface as a Warning diagnostic.
/// </summary>
public sealed class AnalyzerPackSmoke
{
    static string PacksDirectory()
    {
        // Walk up from the test assembly's directory to the repo root, then into _packs/.
        var dir = Path.GetDirectoryName(typeof(AnalyzerPackSmoke).Assembly.Location)!;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "_packs");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate _packs/ relative to the test assembly.");
    }

    [Fact]
    public void AnalyzerPack_ShipsAtCanonicalPath()
    {
        var nupkg = Path.Combine(PacksDirectory(), "Ampelum.Analyzers.0.8.0-rc1.nupkg");
        File.Exists(nupkg).ShouldBeTrue($"Expected pack at {nupkg}");

        using var zip = ZipFile.OpenRead(nupkg);
        var entry = zip.GetEntry("analyzers/dotnet/cs/Ampelum.Analyzers.dll");
        entry.ShouldNotBeNull("NuGet looks for analyzers under analyzers/dotnet/cs/. Without this entry the analyzer never wires into consumer builds.");
    }

    [Fact]
    public async Task PackedAnalyzer_FiresAmp1010_OnFreeFormWithoutOptIn()
    {
        var ct = TestContext.Current.CancellationToken;

        // Extract the analyzer DLL from the published pack and load it.
        var nupkg = Path.Combine(PacksDirectory(), "Ampelum.Analyzers.0.8.0-rc1.nupkg");
        using var zip = ZipFile.OpenRead(nupkg);
        var entry = zip.GetEntry("analyzers/dotnet/cs/Ampelum.Analyzers.dll");
        entry.ShouldNotBeNull();
        byte[] dll;
        using (var stream = entry!.Open())
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms, ct);
            dll = ms.ToArray();
        }
        var analyzerAsm = Assembly.Load(dll);
        var analyzerType = analyzerAsm.GetType("SignalGrid.Ampelum.Analyzers.Amp1010FreeFormOptInAnalyzer");
        analyzerType.ShouldNotBeNull("Pack ships the wrong assembly (AMP1010 type missing).");
        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType!)!;

        // Build a tiny consumer compilation that uses [FreeForm] without [assembly: AllowFreeForm].
        var source = """
            using SignalGrid.Ampelum.Tools;
            public sealed class T
            {
                [FreeForm] public string Q { get; set; } = "";
            }
            """;
        var ampelumCorePath = typeof(SignalGrid.Ampelum.AmpelumContext).Assembly.Location;
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ValueTask).Assembly.Location),
            MetadataReference.CreateFromFile(ampelumCorePath),
            // System.Runtime
            MetadataReference.CreateFromFile(Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        };

        var compilation = CSharpCompilation.Create(
            "ConsumerScenarioC",
            new[] { CSharpSyntaxTree.ParseText(source, cancellationToken: ct) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers(System.Collections.Immutable.ImmutableArray.Create(analyzer));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);

        var amp1010 = diagnostics.Where(d => d.Id == "AMP1010").ToArray();
        amp1010.Length.ShouldBe(1, "AMP1010 must fire exactly once for [FreeForm] without [assembly: AllowFreeForm].");
        amp1010[0].Severity.ShouldBe(DiagnosticSeverity.Warning);
        amp1010[0].GetMessage().ShouldContain("'Q'");
    }
}

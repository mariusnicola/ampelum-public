using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers.Tests.Verifiers;

/// <summary>
/// Minimal hand-rolled diagnostic-emission verifier. Avoids the
/// Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit harness, which targets xunit.v2 only
/// and is not compatible with xunit.v3.
/// </summary>
public static class CSharpAnalyzerVerifier
{
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string source,
        CancellationToken ct)
    {
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();
        // Ensure Ampelum.Tools assembly is referenced.
        refs.Add(MetadataReference.CreateFromFile(typeof(Ampelum.Tools.ITypedTool<>).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTestAssembly",
            syntaxTrees: [tree],
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                reportSuppressedDiagnostics: false));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);
        return diagnostics;
    }
}

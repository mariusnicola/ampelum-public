using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ampelum.Samples.BuildTimeTools.Tests.Verifiers;

// Same technique as tests/Ampelum.Analyzers.Tests/Verifiers/CSharpAnalyzerVerifier.cs,
// plus an in-memory global analyzer config and a chosen assembly name. Both are needed
// by rules that read .editorconfig keys or check the compilation's assembly name.
public static class AnalyzerVerifier
{
    public const string DefaultAssemblyName = "AnalyzerTestAssembly";

    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string source,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? globalConfig = null,
        string assemblyName = DefaultAssemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();
        // Assemblies that are only loaded on first use must be listed, or snippets using them do not bind.
        refs.Add(MetadataReference.CreateFromFile(typeof(SignalGrid.Ampelum.SignatureMetadataCache).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.AI.IChatClient).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [tree],
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var options = new AnalyzerOptions(
            ImmutableArray<AdditionalText>.Empty,
            new ConfigProvider(new GlobalConfig(globalConfig ?? new Dictionary<string, string>())));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer), options);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);
    }

    // Returns the diagnostic's span text and its 1-based line and column in the snippet.
    // Asserting on these pins the node the analyzer reports on, not only the rule id.
    public static (string Text, int Line, int Column) Where(Diagnostic d)
    {
        var start = d.Location.GetLineSpan().StartLinePosition;
        var text = d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan);
        return (text, start.Line + 1, start.Character + 1);
    }

    sealed class GlobalConfig(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        {
            if (values.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
            value = null;
            return false;
        }
    }

    sealed class ConfigProvider(AnalyzerConfigOptions global) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new GlobalConfig(new Dictionary<string, string>());
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new GlobalConfig(new Dictionary<string, string>());
    }
}

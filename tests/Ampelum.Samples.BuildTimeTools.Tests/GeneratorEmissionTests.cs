using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SignalGrid.Ampelum.SourceGenerator;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.BuildTimeTools.Tests;

// Drives the generator directly over the same signature shape the sample uses,
// so a regression shows up here with the emitted text in hand.
public sealed class GeneratorEmissionTests
{
    const string SignatureSource = """
        using SignalGrid.Ampelum;

        [Signature("Summarize the passage in one sentence.")]
        public sealed record Summarize : ISignature
        {
            [Input(Desc = "the text to summarize")]
            public required string Passage { get; init; }

            [Output(Desc = "one sentence")]
            public string Summary { get; init; } = "";
        }
        """;

    [Fact]
    public void PerSignature_EmitsModuleInitializerWithFields()
    {
        var (sources, _) = Run(SignatureSource, assemblyAttribute: "");

        var text = sources.Single(s => s.Name.EndsWith("Summarize_MetadataInitializer.g.cs")).Text;

        text.ShouldContain("[ModuleInitializer]");
        text.ShouldContain("SignatureMetadataCache.Register(");
        text.Split('\n').Select(line => line.Trim()).ShouldContain("typeof(Summarize),");
        text.ShouldContain("\"Summarize the passage in one sentence.\"");
        text.ShouldContain("new Field(\"Passage\", typeof(string), \"the text to summarize\", FieldKind.Input, false)");
        text.ShouldContain("new Field(\"Summary\", typeof(string), \"one sentence\", FieldKind.Output, false)");
    }

    [Fact]
    public void PerSignature_OutputCompiles()
    {
        var (_, diagnostics) = Run(SignatureSource, assemblyAttribute: "");

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Fact]
    public void WithoutAssemblyAttribute_NoAccessorsAreEmitted()
    {
        var (sources, _) = Run(SignatureSource, assemblyAttribute: "");

        sources.ShouldNotContain(s => s.Name == "__AmpelumSignatureAccessors.g.cs");
    }

    [Fact]
    public void WithAssemblyAttribute_EmitsTypedGettersForInputs()
    {
        var (sources, diagnostics) = Run(SignatureSource,
            assemblyAttribute: "[assembly: GenerateSignatureMetadata]");

        var text = sources.Single(s => s.Name == "__AmpelumSignatureAccessors.g.cs").Text;

        text.ShouldContain("SignatureMetadataCache.RegisterAccessors<global::Summarize>");
        text.ShouldContain("static (global::Summarize s) => s.Passage");
        text.ShouldNotContain("s.Summary");
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    static (List<(string Name, string Text)> Sources, List<Diagnostic> Diagnostics) Run(
        string signature, string assemblyAttribute)
    {
        // Assembly attributes must follow the using directives, so the attribute goes after the using line.
        var source = signature.Replace("using SignalGrid.Ampelum;", "using SignalGrid.Ampelum;\n" + assemblyAttribute);
        var compilation = CSharpCompilation.Create(
            assemblyName: "SampleSignatures",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new SignatureMetadataGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var generatorDiagnostics);

        var sources = driver.GetRunResult().GeneratedTrees
            .Select(t => (Name: Path.GetFileName(t.FilePath), Text: t.GetText().ToString()))
            .ToList();

        var diagnostics = outputCompilation.GetDiagnostics().ToList();
        diagnostics.AddRange(generatorDiagnostics);
        return (sources, diagnostics);
    }

    static List<MetadataReference> References()
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(SignalGrid.Ampelum.SignatureMetadataCache).Assembly.Location),
        };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in new[] { "System.Runtime.dll", "System.Collections.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        return refs;
    }
}

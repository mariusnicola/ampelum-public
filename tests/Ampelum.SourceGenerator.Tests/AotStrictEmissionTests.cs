using System.Collections.Generic;
using System.IO;
using System.Linq;
using SignalGrid.Ampelum.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace Ampelum.SourceGenerator.Tests;

public sealed class AotStrictEmissionTests
{
    const string Default = """
        using SignalGrid.Ampelum;
        namespace U;
        [Signature("inst")]
        public sealed record Sig : ISignature
        {
            [Input] public required string Q { get; init; }
            [Output] public string A { get; init; } = "";
        }
        """;

    const string AotStrict = """
        using SignalGrid.Ampelum;
        [assembly: GenerateSignatureMetadata]
        namespace U;
        [Signature("inst")]
        public sealed record Sig : ISignature
        {
            [Input] public required string Q { get; init; }
            [Output] public string A { get; init; } = "";
        }
        """;

    [Fact]
    public void DefaultMode_DoesNotEmitAccessorFile()
    {
        var trees = RunGenerator(Default);
        trees.ShouldNotContain(t => t.FilePath.EndsWith("__AmpelumSignatureAccessors.g.cs"));
        trees.ShouldContain(t => t.FilePath.EndsWith("_MetadataInitializer.g.cs"));
    }

    [Fact]
    public void AotStrictMode_EmitsAccessorFile()
    {
        var trees = RunGenerator(AotStrict);
        var accessor = trees.SingleOrDefault(t =>
            t.FilePath.EndsWith("__AmpelumSignatureAccessors.g.cs"));
        accessor.ShouldNotBeNull();
        var src = accessor!.ToString();
        src.ShouldContain("RegisterAccessors<global::U.Sig>");
        src.ShouldContain("static (global::U.Sig s) => s.Q");
        src.ShouldContain("[ModuleInitializer]");
    }

    [Fact]
    public void AotStrictMode_StillEmitsPerSignatureInitializer()
    {
        var trees = RunGenerator(AotStrict);
        trees.ShouldContain(t => t.FilePath.EndsWith("_MetadataInitializer.g.cs"));
    }

    static IReadOnlyList<SyntaxTree> RunGenerator(string source)
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(SignalGrid.Ampelum.SignatureMetadataCache).Assembly.Location),
        };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in new[] { "System.Runtime.dll", "System.Collections.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "TestAsm",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var generator = new SignatureMetadataGenerator();
        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        return driver.GetRunResult().GeneratedTrees;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SignalGrid.Ampelum.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

/// <summary>
/// Unit tests for <see cref="SignatureMetadataGenerator"/>.
/// Each test builds a minimal CSharpCompilation with the generator applied
/// and inspects the resulting generated source trees.
/// </summary>
public sealed class SignatureMetadataGeneratorTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Runs the generator against <paramref name="source"/> and returns all generated
    /// source texts keyed by hint name.
    /// </summary>
    static Dictionary<string, string> RunGenerator(string source, out GeneratorDriverRunResult result)
    {
        var compilation = CreateCompilation(source);
        var generator = new SignatureMetadataGenerator();
        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        result = driver.GetRunResult();
        return result.GeneratedTrees
            .ToDictionary(
                t => Path.GetFileName(t.FilePath),
                t => t.GetText().ToString());
    }

    static CSharpCompilation CreateCompilation(string source)
    {
        var references = BuildMetadataReferences();
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static List<MetadataReference> BuildMetadataReferences()
    {
        var refs = new List<MetadataReference>();

        // Core BCL references
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IReadOnlyList<>).Assembly.Location));

        // Add runtime directory assemblies commonly needed
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in new[] { "System.Runtime.dll", "System.Collections.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        // Ampelum reference
        refs.Add(MetadataReference.CreateFromFile(typeof(SignalGrid.Ampelum.SignatureMetadataCache).Assembly.Location));

        return refs;
    }

    // ---------------------------------------------------------------------------
    // Core happy-path test
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_EmitsModuleInitializer_PerSignatureType()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Answer questions.")]
            public record Qa : ISignature
            {
                [Input(Desc = "the question")] public required string Question { get; init; }
                [Output(Desc = "1-5 words")]   public string Answer { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out var result);

        result.Diagnostics.ShouldBeEmpty();

        var hint = files.Keys.FirstOrDefault(k => k.Contains("Qa_MetadataInitializer"));
        hint.ShouldNotBeNull();

        var text = files[hint!];
        text.ShouldContain("[ModuleInitializer]");
        text.ShouldContain("SignatureMetadataCache.Register(");
        text.ShouldContain("typeof(MyApp.Qa)");
        text.ShouldContain("\"Answer questions.\"");
        text.ShouldContain("FieldKind.Input");
        text.ShouldContain("FieldKind.Output");
        text.ShouldContain("\"the question\"");
        text.ShouldContain("\"1-5 words\"");
        text.ShouldContain("public static void Initialize()");
    }

    // ---------------------------------------------------------------------------
    // Skip types without [Signature]
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_SkipsTypesWithoutSignatureAttribute()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            public record Plain : ISignature
            {
                [Input]  public required string X { get; init; }
                [Output] public string Y { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out var result);

        result.Diagnostics.ShouldBeEmpty();
        files.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Skip if no inputs
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_SkipsTypesWithNoInputs()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("No inputs.")]
            public record OnlyOutputs : ISignature
            {
                [Output] public string Y { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out var result);

        result.Diagnostics.ShouldBeEmpty();
        files.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Skip if no outputs
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_SkipsTypesWithNoOutputs()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("No outputs.")]
            public record OnlyInputs : ISignature
            {
                [Input] public required string X { get; init; }
            }
            """;

        var files = RunGenerator(source, out var result);

        result.Diagnostics.ShouldBeEmpty();
        files.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Desc property forwarded
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_DescriptionUsesDescProperty()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Task.")]
            public record Described : ISignature
            {
                [Input(Desc  = "custom input desc")]  public required string In1 { get; init; }
                [Output(Desc = "custom output desc")] public string Out1 { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        text.ShouldContain("\"custom input desc\"");
        text.ShouldContain("\"custom output desc\"");
    }

    // ---------------------------------------------------------------------------
    // Desc fallback to property name when not set
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_DescriptionFallsBackToPropertyName_WhenDescNotSet()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Task.")]
            public record NoDesc : ISignature
            {
                [Input]  public required string Question { get; init; }
                [Output] public string Answer { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        // Falls back to member name when Desc is not specified
        text.ShouldContain("\"Question\"");
        text.ShouldContain("\"Answer\"");
    }

    // ---------------------------------------------------------------------------
    // Null/empty instructions
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_EmptyInstructions_EmitsEmptyStringLiteral()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("")]
            public record EmptyInstr : ISignature
            {
                [Input]  public required string X { get; init; }
                [Output] public string Y { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        // Empty instructions should emit ""
        text.ShouldContain(", \"\",");
    }

    // ---------------------------------------------------------------------------
    // Nullable (optional) fields produce IsOptional = true
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_NullableField_SetsIsOptionalTrue()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Task.")]
            public record WithNullable : ISignature
            {
                [Input]  public required string Required { get; init; }
                [Output] public string? Nullable { get; init; }
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        text.ShouldContain(", false)");  // Required -> IsOptional = false
        text.ShouldContain(", true)");   // Nullable? -> IsOptional = true
    }

    // ---------------------------------------------------------------------------
    // Non-nullable fields produce IsOptional = false
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_NonNullableField_SetsIsOptionalFalse()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Task.")]
            public record AllRequired : ISignature
            {
                [Input]  public required string X { get; init; }
                [Output] public required string Y { get; init; }
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        // Both should be non-optional
        text.ShouldNotContain(", true)");
    }

    // ---------------------------------------------------------------------------
    // Multiple signatures produce multiple files
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_MultipleSignatures_EmitsOneFileEach()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("First.")]
            public record First : ISignature
            {
                [Input]  public required string A { get; init; }
                [Output] public string B { get; init; } = "";
            }

            [Signature("Second.")]
            public record Second : ISignature
            {
                [Input]  public required string C { get; init; }
                [Output] public string D { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        files.Count.ShouldBe(2);
        files.Keys.ShouldContain(k => k.Contains("First_MetadataInitializer"));
        files.Keys.ShouldContain(k => k.Contains("Second_MetadataInitializer"));
    }

    // ---------------------------------------------------------------------------
    // Special characters in instructions string literal escaping
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_EscapesSpecialCharsInInstructions()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Say \"hello\" and \\world\\.")]
            public record Special : ISignature
            {
                [Input]  public required string X { get; init; }
                [Output] public string Y { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        // The generated code must not break the C# string literal
        text.ShouldContain("\\\"hello\\\"");
    }

    // ---------------------------------------------------------------------------
    // Non-record type with [Signature] is filtered by predicate (syntax predicate fires on record only)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_SkipsNonRecordClassWithSignatureAttribute()
    {
        // The predicate `node is RecordDeclarationSyntax` filters out plain classes.
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Not a record.")]
            public class NotARecord : ISignature
            {
                [Input]  public string X { get; set; } = "";
                [Output] public string Y { get; set; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        files.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Hint name uses fully-qualified name to avoid collisions
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_HintNameUsesFullyQualifiedName()
    {
        const string source = """
            using SignalGrid.Ampelum;

            namespace NS1
            {
                [Signature("Task A.")]
                public record MyRecord : ISignature
                {
                    [Input]  public required string X { get; init; }
                    [Output] public string Y { get; init; } = "";
                }
            }

            namespace NS2
            {
                [Signature("Task B.")]
                public record MyRecord : ISignature
                {
                    [Input]  public required string A { get; init; }
                    [Output] public string B { get; init; } = "";
                }
            }
            """;

        var files = RunGenerator(source, out _);

        // Both should be emitted â€” different fully-qualified names produce different hints
        files.Count.ShouldBe(2);
        // Hint names should be distinct
        files.Keys.Distinct().Count().ShouldBe(2);
    }

    // ---------------------------------------------------------------------------
    // Generated code contains auto-generated header
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_EmittedCode_HasAutoGeneratedHeader()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("x")]
            public record Sig : ISignature
            {
                [Input]  public required string I { get; init; }
                [Output] public string O { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        files.Values.Single().ShouldStartWith("// <auto-generated/>");
    }

    // ---------------------------------------------------------------------------
    // Generated code is in SignalGrid.Ampelum.Generated namespace
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_EmittedCode_IsInSignalGridAmpelumGeneratedNamespace()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("x")]
            public record NS : ISignature
            {
                [Input]  public required string I { get; init; }
                [Output] public string O { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        files.Values.Single().ShouldContain("namespace SignalGrid.Ampelum.Generated;");
    }

    // ---------------------------------------------------------------------------
    // Property with neither [Input] nor [Output] is skipped by PropertyKind -> null path
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_SkipsPropertiesWithNoInputOrOutputAttribute()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Task.")]
            public record WithExtra : ISignature
            {
                [Input]  public required string X { get; init; }
                // This property has no Input/Output attribute and must be ignored
                public string Hidden { get; init; } = "";
                [Output] public string Y { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        // Only X and Y should appear in the generated fields; Hidden should be absent
        text.ShouldNotContain("\"Hidden\"");
        text.ShouldContain("\"X\"");
        text.ShouldContain("\"Y\"");
    }

    // ---------------------------------------------------------------------------
    // Output-only property (exercises OutputAttribute branch in PropertyKind at line 93)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_OutputPropertyKind_IsCorrectlyLabelled()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Task.")]
            public record OutputOnly : ISignature
            {
                [Input]  public required string In { get; init; }
                [Output] public string Out { get; init; } = "";
            }
            """;

        var files = RunGenerator(source, out _);

        var text = files.Values.Single();
        text.ShouldContain("FieldKind.Output");
        // Ensure output descriptor is present
        text.ShouldContain("\"Out\"");
    }

    // ---------------------------------------------------------------------------
    // Emitted code actually compiles without errors
    // ---------------------------------------------------------------------------

    [Fact]
    public void Generator_EmittedCode_Compiles()
    {
        const string source = """
            using SignalGrid.Ampelum;
            namespace MyApp;

            [Signature("Answer questions about a topic.")]
            public record Qa : ISignature
            {
                [Input(Desc  = "the question to answer")] public required string Question { get; init; }
                [Output(Desc = "the answer")]             public string Answer { get; init; } = "";
                [Output]                                  public string? Confidence { get; init; }
            }
            """;

        var ct = Xunit.TestContext.Current.CancellationToken;
        var compilation = CreateCompilation(source);
        var generator = new SignatureMetadataGenerator();
        CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generatorDiagnostics, ct);

        generatorDiagnostics.ShouldBeEmpty();
        var errors = updated.GetDiagnostics(ct)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        errors.ShouldBeEmpty();
    }
}

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP011: a type exposes more than N public methods (default 10).
/// Use .editorconfig <c>dotnet_diagnostic.AMP011.max_public_methods = N</c> to configure.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp011MaxPublicMethodsAnalyzer : DiagnosticAnalyzer
{
    const int DefaultMaxMethods = 10;

    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp011,
        title: "Type has too many public methods",
        messageFormat: "Type '{0}' has {1} public methods, which exceeds the maximum of {2}",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Types with many public methods indicate poor cohesion. Consider splitting into smaller types.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
    }

    static void AnalyzeType(SymbolAnalysisContext ctx)
    {
        var type = (INamedTypeSymbol)ctx.Symbol;
        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct) return;

        var maxMethods = GetMaxMethods(ctx.Options);
        var publicMethods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Count(m => m.DeclaredAccessibility == Accessibility.Public
                && m.MethodKind == MethodKind.Ordinary);

        if (publicMethods <= maxMethods) return;

        ctx.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0],
            type.Name, publicMethods, maxMethods));
    }

    static int GetMaxMethods(AnalyzerOptions options)
    {
        if (options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "dotnet_diagnostic.AMP011.max_public_methods", out var raw)
            && int.TryParse(raw, out var parsed) && parsed > 0)
            return parsed;
        return DefaultMaxMethods;
    }
}

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP015: a type that implements <c>ISignature</c> is missing the <c>[Signature]</c> attribute.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp015SignatureRequiredAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp015,
        title: "ISignature implementation missing [Signature] attribute",
        messageFormat: "Type '{0}' implements ISignature but is not annotated with [Signature]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "All ISignature implementations must be annotated with [Signature] for the source generator.");

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
        if (type.IsAbstract) return;

        var sigInterface = ctx.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.ISignature");
        if (sigInterface is null) return;

        if (!type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, sigInterface))) return;

        var sigAttr = ctx.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.SignatureAttribute");
        if (sigAttr is null) return;

        if (type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, sigAttr))) return;

        ctx.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0], type.Name));
    }
}

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP002: an <c>ITypedTool</c> implementation lacks <c>[ToolCategory]</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp002ToolCategoryAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp002,
        title: "ITypedTool implementation is not classified by [ToolCategory]",
        messageFormat: "Type '{0}' implements ITypedTool but lacks [ToolCategory]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Tool category enables future per-category budget gating (PSQ-7).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    static void Analyze(SymbolAnalysisContext ctx)
    {
        var type = (INamedTypeSymbol)ctx.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;
        var typedToolBase = ctx.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.Tools.ITypedTool`1");
        if (typedToolBase is null) return;
        var implements = type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, typedToolBase));
        if (!implements) return;
        var attr = ctx.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.Tools.ToolCategoryAttribute");
        if (attr is null) return;
        if (type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attr))) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0], type.Name));
    }
}

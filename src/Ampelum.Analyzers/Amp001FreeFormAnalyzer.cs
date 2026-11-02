using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP001: an <c>ITypedTool</c> implementation has a <c>string</c> property without <c>[FreeForm]</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp001FreeFormAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp001,
        title: "ITypedTool string parameter is not [FreeForm]",
        messageFormat: "Property '{0}' is a string on ITypedTool '{1}' and is missing [FreeForm]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Free-form string parameters on tools invite hallucination and injection. Spec §3.2.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
    }

    static void AnalyzeSymbol(SymbolAnalysisContext ctx)
    {
        var type = (INamedTypeSymbol)ctx.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;
        if (!ImplementsITypedTool(type)) return;

        var freeFormAttr = ctx.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.Tools.FreeFormAttribute");
        if (freeFormAttr is null) return;

        foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.Name is "Name" or "Description") continue;
            if (member.Type.SpecialType != SpecialType.System_String) continue;
            if (member.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, freeFormAttr))) continue;
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, member.Locations[0], member.Name, type.Name));
        }
    }

    static bool ImplementsITypedTool(INamedTypeSymbol type)
        => type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "SignalGrid.Ampelum.Tools.ITypedTool<TResult>");
}

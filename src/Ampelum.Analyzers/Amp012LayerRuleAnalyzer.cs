using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP012: namespace layer rule violation — a type in a lower layer imports a type from a higher layer.
/// Default ordering: Controllers → Services → DataAccess (Descending by layer).
/// Use .editorconfig <c>dotnet_diagnostic.AMP012.layer_order = Layer1;Layer2;Layer3</c> to configure.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp012LayerRuleAnalyzer : DiagnosticAnalyzer
{
    static readonly string[] DefaultLayerOrder = ["Controllers", "Services", "DataAccess"];

    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp012,
        title: "Namespace layer rule violation",
        messageFormat: "'{0}' (in layer '{1}') imports '{2}' (from higher layer '{3}') — dependency direction violation",
        category: "Architecture",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Types must only reference types from the same or lower layers.");

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
        if (type.TypeKind != TypeKind.Class) return;

        var layerOrder = GetLayerOrder(ctx.Options);
        var typeLayer = DetectLayer(type.ContainingNamespace?.ToDisplayString() ?? "", layerOrder);
        if (typeLayer < 0) return;

        var usedTypes = type.GetMembers()
            .SelectMany(m => m is IMethodSymbol ms ? ms.Parameters : [])
            .Select(p => p.Type)
            .Concat(type.GetMembers().OfType<IFieldSymbol>().Select(f => f.Type))
            .Concat(type.GetMembers().OfType<IPropertySymbol>().Select(p => p.Type))
            .Distinct(SymbolEqualityComparer.Default);

        foreach (var used in usedTypes)
        {
            if (used is not INamedTypeSymbol usedType) continue;
            if (usedType.ContainingAssembly?.Name != type.ContainingAssembly?.Name) continue;

            var usedLayer = DetectLayer(usedType.ContainingNamespace?.ToDisplayString() ?? "", layerOrder);
            if (usedLayer < 0) continue;
            if (usedLayer <= typeLayer) continue;

            ctx.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0],
                type.Name, layerOrder[typeLayer],
                usedType.Name, layerOrder[usedLayer]));
        }
    }

    static int DetectLayer(string ns, string[] order)
    {
        for (int i = 0; i < order.Length; i++)
        {
            if (ns.Contains("." + order[i]) || ns.EndsWith("." + order[i]) || ns == order[i])
                return i;
        }
        return -1;
    }

    static string[] GetLayerOrder(AnalyzerOptions options)
    {
        if (options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "dotnet_diagnostic.AMP012.layer_order", out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (parts.Length > 1) return parts;
        }
        return DefaultLayerOrder;
    }
}

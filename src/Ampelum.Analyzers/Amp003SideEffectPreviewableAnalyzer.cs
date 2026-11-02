using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP003: an <c>ITypedTool</c> whose name suggests side effects (Send/Delete/Write/Update/Insert/Drop/Exec)
/// but does not implement <c>IPreviewable</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp003SideEffectPreviewableAnalyzer : DiagnosticAnalyzer
{
    static readonly string[] Prefixes = { "Send", "Delete", "Write", "Update", "Insert", "Drop", "Exec" };

    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp003,
        title: "Side-effecting ITypedTool does not implement IPreviewable",
        messageFormat: "Type '{0}' looks side-effecting (prefix '{1}') but does not implement IPreviewable",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Spec §3.2: side-effecting tools must be previewable.");

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
        if (!type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, typedToolBase))) return;

        var matchedPrefix = Prefixes.FirstOrDefault(p => type.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (matchedPrefix is null) return;

        var previewable = ctx.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.Tools.IPreviewable");
        if (previewable is null) return;
        if (type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, previewable))) return;

        ctx.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0], type.Name, matchedPrefix));
    }
}

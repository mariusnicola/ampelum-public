using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP1010: a member is annotated with <c>[FreeForm]</c> in an assembly that has not opted in
/// via <c>[assembly: AllowFreeForm]</c>. Free-form parameters expand the prompt-injection
/// surface area and should be a deliberate choice.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp1010FreeFormOptInAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp1010,
        title: "[FreeForm] used in an assembly without [assembly: AllowFreeForm]",
        messageFormat: "'{0}' is marked [FreeForm] but the assembly is missing [assembly: Ampelum.Tools.AllowFreeForm]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Add [assembly: Ampelum.Tools.AllowFreeForm] to acknowledge the increased prompt-injection surface area, or remove the [FreeForm] attribute and use a typed parameter instead.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var freeFormAttr = start.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.Tools.FreeFormAttribute");
            var allowAttr = start.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.Tools.AllowFreeFormAttribute");
            if (freeFormAttr is null || allowAttr is null) return;

            var assemblyOptedIn = start.Compilation.Assembly.GetAttributes()
                .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, allowAttr));
            if (assemblyOptedIn) return;

            start.RegisterSymbolAction(ctx => Analyze(ctx, freeFormAttr), SymbolKind.Property, SymbolKind.Parameter);
        });
    }

    static void Analyze(SymbolAnalysisContext ctx, INamedTypeSymbol freeFormAttr)
    {
        var symbol = ctx.Symbol;
        var match = symbol.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, freeFormAttr));
        if (match is null) return;
        var loc = symbol.Locations.FirstOrDefault() ?? Location.None;
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, loc, symbol.Name));
    }
}

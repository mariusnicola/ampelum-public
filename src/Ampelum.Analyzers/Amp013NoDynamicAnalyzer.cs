using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP013: the <c>dynamic</c> keyword is used in a type member. Dynamic is not AOT-safe.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp013NoDynamicAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp013,
        title: "Use of 'dynamic' keyword — not AOT-safe",
        messageFormat: "'dynamic' keyword used in '{0}' — Ampelum must remain AOT-compatible",
        category: "Compatibility",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The dynamic keyword prevents AOT compilation. Use object with reflection or a typed approach instead.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeNode, SyntaxKind.IdentifierName);
    }

    static void AnalyzeNode(SyntaxNodeAnalysisContext ctx)
    {
        var identifier = (IdentifierNameSyntax)ctx.Node;
        if (identifier.Identifier.Text != "dynamic") return;

        // Only flag if it resolves to the 'dynamic' type (not a local variable named "dynamic")
        var typeInfo = ctx.SemanticModel.GetTypeInfo(identifier, ctx.CancellationToken);
        if (typeInfo.Type is not { TypeKind: TypeKind.Dynamic }) return;

        var enclosing = ctx.Node.Ancestors()
            .FirstOrDefault(a => a is MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax or VariableDeclarationSyntax);
        var name = enclosing switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            _ => "member"
        };

        ctx.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), name));
    }
}

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP014: raw LLM prompt construction detected — use <c>Predict</c> instead.
/// Flags direct calls to <c>GetResponseAsync</c>, <c>ChatMessage</c> constructor, and <c>Predict</c> outside of Agent abstraction.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp014NoBareLlmPromptsAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp014,
        title: "Bare LLM prompt construction — use Predict<TSig>",
        messageFormat: "Raw LLM API call '{0}' — use Predict<TSig> instead",
        category: "Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Direct LLM calls bypass Agent abstractions. Use Predict<TSig> for structured, observable prompts.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Skip if assembly has opted out
            var allowAttr = start.Compilation.GetTypeByMetadataName("SignalGrid.Ampelum.Analyzers.AllowBareLlmPromptsAttribute");
            if (allowAttr is not null)
            {
                var assemblyAllowed = start.Compilation.Assembly.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, allowAttr));
                if (assemblyAllowed) return;
            }

            start.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        });
    }

    static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;

        var name = member.Name.Identifier.Text;
        if (name is not ("GetResponseAsync" or "Predict")) return;

        var symbol = ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol;
        if (symbol is null) return;

        // Only flag if it's from Microsoft.Extensions.AI (IChatClient.GetResponseAsync)
        if (name == "GetResponseAsync" &&
            symbol.ContainingType?.ContainingNamespace?.ToDisplayString() == "Microsoft.Extensions.AI")
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "IChatClient.GetResponseAsync"));
        }
        // Flag Predict<> if it's from SignalGrid.Ampelum but outside Agent package
        else if (name == "Predict" &&
            symbol.ContainingType?.ContainingNamespace?.ToDisplayString() == "SignalGrid.Ampelum" &&
            !ctx.Compilation.Assembly.Name.Contains("SignalGrid.Ampelum.Agent"))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Predict<>"));
        }
    }

    static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx)
    {
        var creation = (ObjectCreationExpressionSyntax)ctx.Node;
        var typeInfo = ctx.SemanticModel.GetTypeInfo(creation, ctx.CancellationToken);
        if (typeInfo.Type?.Name != "ChatMessage") return;

        var ns = typeInfo.Type.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns != "Microsoft.Extensions.AI") return;

        ctx.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "new ChatMessage(role, content)"));
    }
}

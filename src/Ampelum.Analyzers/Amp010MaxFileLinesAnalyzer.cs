using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SignalGrid.Ampelum.Analyzers;

/// <summary>
/// AMP010: file exceeds max lines threshold (default 400). Detected via token-count heuristic.
/// Use .editorconfig <c>dotnet_diagnostic.AMP010.max_file_lines = N</c> to configure.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Amp010MaxFileLinesAnalyzer : DiagnosticAnalyzer
{
    const int DefaultMaxLines = 400;

    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.Amp010,
        title: "File exceeds maximum line count",
        messageFormat: "File has {0} lines, which exceeds the maximum of {1}",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Large files indicate poor separation of concerns. Consider splitting into smaller files.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeTree);
    }

    static void AnalyzeTree(SyntaxTreeAnalysisContext ctx)
    {
        var maxLines = GetMaxLines(ctx.Options);
        var lineCount = ctx.Tree.GetText().Lines.Count;
        if (lineCount <= maxLines) return;

        var lastLine = ctx.Tree.GetRoot().GetLastToken().GetLocation();
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, lastLine, lineCount, maxLines));
    }

    static int GetMaxLines(AnalyzerOptions options)
    {
        if (options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "dotnet_diagnostic.AMP010.max_file_lines", out var raw)
            && int.TryParse(raw, out var parsed) && parsed > 0)
            return parsed;
        return DefaultMaxLines;
    }
}

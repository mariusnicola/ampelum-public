using SignalGrid.Ampelum.Tools;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace SignalGrid.Ampelum.Tools.CodeInterpreter;

/// <summary>
/// ITypedTool: evaluates a C# expression / script using Roslyn-Scripting in the host process.
/// <strong>Unsandboxed</strong>. Full file-system and network access. Do NOT run on untrusted input.
/// POLC tags <c>compute:host-process</c>, <c>risk:high</c>, <c>network:any</c>, <c>fs:any</c>.
/// </summary>
[ToolCategory(ToolCategoryKind.Compute)]
public sealed class CodeInterpreterTool : ITypedTool<CodeResult>
{
    /// <summary>Creates the tool.</summary>
    /// <param name="code">C# script source. Last expression's value is returned.</param>
    public CodeInterpreterTool(string code) => Code = code;

    /// <inheritdoc/>
    public string Name => "code_interpreter";
    /// <inheritdoc/>
    public string Description => "Evaluates a C# expression or script using Roslyn-Scripting (unsandboxed; host-process).";

    /// <summary>The script source.</summary>
    [FreeForm]
    public string Code { get; }

    /// <inheritdoc/>
    public async ValueTask<CodeResult> InvokeAsync(CancellationToken ct)
    {
        var options = ScriptOptions.Default
            .WithReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(System.Collections.Generic.List<>).Assembly)
            .WithImports("System", "System.Linq", "System.Collections.Generic");
        var value = await CSharpScript.EvaluateAsync<object?>(Code, options, cancellationToken: ct).ConfigureAwait(false);
        return new CodeResult(value, value?.ToString() ?? "");
    }
}

/// <summary>Result of a <see cref="CodeInterpreterTool"/> evaluation.</summary>
/// <param name="Value">The expression's runtime value (boxed); may be <see langword="null"/>.</param>
/// <param name="Display">A string rendering of <paramref name="Value"/>.</param>
public sealed record CodeResult(object? Value, string Display);

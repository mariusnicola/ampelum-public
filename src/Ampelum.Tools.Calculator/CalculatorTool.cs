using System.Data;
using System.Globalization;
using SignalGrid.Ampelum.Tools;

namespace SignalGrid.Ampelum.Tools.Calculator;

/// <summary>
/// ITypedTool: pure-compute arithmetic evaluator. Uses <see cref="DataTable.Compute"/> for safe
/// expression evaluation (no method calls, no I/O). POLC tags `compute:pure`, `data:none`.
/// </summary>
[ToolCategory(ToolCategoryKind.Compute)]
public sealed class CalculatorTool : ITypedTool<CalcResult>
{
    /// <summary>Creates the tool.</summary>
    /// <param name="expression">Arithmetic expression (e.g. <c>"3.14159 * 5 * 5"</c>).</param>
    public CalculatorTool(string expression) => Expression = expression;

    /// <inheritdoc/>
    public string Name => "calculator";
    /// <inheritdoc/>
    public string Description => "Evaluates a pure arithmetic expression and returns the numeric value.";

    /// <summary>The expression to evaluate.</summary>
    [FreeForm]
    public string Expression { get; }

    /// <inheritdoc/>
    public ValueTask<CalcResult> InvokeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var table = new DataTable();
        var raw = table.Compute(Expression, string.Empty);
        var value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        return ValueTask.FromResult(new CalcResult(value, Expression));
    }
}

/// <summary>Result of <see cref="CalculatorTool"/>.</summary>
/// <param name="Value">Numeric result.</param>
/// <param name="Expression">Original expression.</param>
public sealed record CalcResult(double Value, string Expression);

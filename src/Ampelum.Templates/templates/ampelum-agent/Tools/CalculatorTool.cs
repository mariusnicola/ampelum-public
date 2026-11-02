using SignalGrid.Ampelum.Tools;

namespace Company.Agent.Tools;

/// <summary>Stub calculator tool — sums Lhs and Rhs. Replace with NCalc for full expressions.</summary>
public sealed class CalculatorTool : ITypedTool<double>
{
    public string Name => "calculator";
    public string Description => "Adds two numbers (stub).";

    public double Lhs { get; set; }
    public double Rhs { get; set; }

    public ValueTask<double> InvokeAsync(CancellationToken ct)
        => ValueTask.FromResult(Lhs + Rhs);
}

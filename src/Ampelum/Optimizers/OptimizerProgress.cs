namespace SignalGrid.Ampelum.Optimizers;

/// <summary>Progress report emitted by <see cref="IOptimizer{TProgram}.CompileAsync"/>.</summary>
public sealed record OptimizerProgress(
    string Phase,
    int Step,
    int TotalSteps,
    double BestScore,
    IReadOnlyDictionary<string, object?>? Extras = null)
{
    /// <summary>Extra key-value metadata emitted by specific optimizers.</summary>
    public IReadOnlyDictionary<string, object?> Extras { get; init; } =
        Extras ?? new Dictionary<string, object?>();
}

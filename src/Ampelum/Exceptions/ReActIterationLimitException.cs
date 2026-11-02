namespace SignalGrid.Ampelum.Exceptions;

/// <summary>Thrown when ReAct exhausts <c>MaxIterations</c> without producing a final answer.</summary>
public sealed class ReActIterationLimitException(int maxIterations, string message)
    : AmpelumException(message)
{
    /// <summary>The <c>MaxIterations</c> limit that was reached.</summary>
    public int MaxIterations { get; } = maxIterations;
}

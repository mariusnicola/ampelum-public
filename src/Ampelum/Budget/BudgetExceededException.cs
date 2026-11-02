namespace SignalGrid.Ampelum.Budget;

/// <summary>Thrown by <see cref="IBudget.Consume"/> and <c>BudgetHook.OnBefore</c> when any limit is breached.</summary>
public sealed class BudgetExceededException : InvalidOperationException
{
    /// <summary>Snapshot of the budget state at the moment the breach was detected.</summary>
    public BudgetSnapshot Snapshot { get; }

    /// <summary>Initialises a new <see cref="BudgetExceededException"/>.</summary>
    public BudgetExceededException(BudgetSnapshot snapshot)
        : base($"Budget exceeded: {snapshot}") => Snapshot = snapshot;
}

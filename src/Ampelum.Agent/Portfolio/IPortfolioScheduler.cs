namespace SignalGrid.Ampelum.Agent;

/// <summary>Cron-style scheduler for portfolio runs.
/// Persists schedules to a JSON file and triggers IPortfolioOrchestrator when due.
/// Report sanitization is injected as a delegate so Ampelum.Agent keeps its
/// zero-dependency-on-Ampelum invariant.</summary>
public interface IPortfolioScheduler
{
    /// <summary>Register a new schedule. Returns the assigned ScheduleId.</summary>
    ScheduleId Schedule(string intent, PortfolioOptions options, ScheduleExpression expression);

    /// <summary>Remove a schedule by id. No-op if not found.</summary>
    void Remove(ScheduleId id);

    /// <summary>List all registered schedules (order not guaranteed).</summary>
    IReadOnlyList<ScheduleEntry> List();

    /// <summary>Check all schedules and run any that are due at the given time.
    /// Uses DateTimeOffset.UtcNow if no explicit now is provided.
    /// Writes a per-project report to the workspace root for every executed schedule.</summary>
    Task RunPendingAsync(IProjectPortfolio portfolio, DateTimeOffset? now = null);
}
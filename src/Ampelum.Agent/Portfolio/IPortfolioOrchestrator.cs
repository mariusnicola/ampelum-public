namespace SignalGrid.Ampelum.Agent;

/// <summary>Fan-out orchestration across all registered portfolio workspaces.
/// Supports one-shot execution and streaming progress for interactive/REPL modes.</summary>
public interface IPortfolioOrchestrator
{
    /// <summary>Fan out intent across all registered workspaces (or filtered subset).
    /// Respects MaxConcurrentProjects via SemaphoreSlim.</summary>
    Task<PortfolioResult> RunAsync(
        string intent,
        PortfolioOptions options,
        CancellationToken ct = default);

    /// <summary>Stream progress events for interactive/REPL mode.</summary>
    IAsyncEnumerable<PortfolioProgressEvent> StreamAsync(
        string intent,
        PortfolioOptions options,
        CancellationToken ct = default);
}
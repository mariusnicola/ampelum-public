namespace Ampelum.IntegrationTests;

/// <summary>
/// Performance budgets pulled verbatim from the Ampelum v1.0 consolidated spec §D.
/// Used by Pareto integration tests to assert that core paths stay within the
/// shipped contract.
/// </summary>
public static class PerfBudgets
{
    // D1 — Predict happy path with no hooks.
    public static readonly TimeSpan PredictNoHooks = TimeSpan.FromMicroseconds(15);
    public const long PredictNoHooksAllocBytes = 512;

    // D2 — Four-hook pipeline overhead.
    public static readonly TimeSpan FourHookOverhead = TimeSpan.FromMicroseconds(10);

    // D3 — Full pipeline allocation budget.
    public const long FullPipelineAllocBytes = 1024;

    // D4 — AOT cold start.
    public static readonly TimeSpan AotColdStart = TimeSpan.FromMilliseconds(30);

    // D5 — N-parallel p99 multiplier vs serial p50.
    public const double NParallelP99Multiplier = 1.5;

    // D6 — Optimizer compile budgets.
    public static readonly TimeSpan BootstrapFewShot50 = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MiproV2_200x3x3 = TimeSpan.FromSeconds(2);

    // D7 — Streaming throughput.
    public const double StreamingChunksPerSecondMin = 100_000;
    public static readonly TimeSpan StreamingFinalize = TimeSpan.FromMicroseconds(500);

    // Per-test budgets.
    public static readonly TimeSpan AgentTurnP50 = TimeSpan.FromMilliseconds(2);
    public static readonly TimeSpan RetrievalP99 = TimeSpan.FromMilliseconds(5);
    public static readonly TimeSpan AuditPerEntry = TimeSpan.FromMilliseconds(5);
    public static readonly TimeSpan PersistenceRestore = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan ProviderCancellation = TimeSpan.FromMilliseconds(50);
}

using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.IntegrationTests.Fixtures;

/// <summary>
/// Shared per-class fixture for Pareto integration tests. Wires a deterministic
/// in-memory chat fake plus an in-memory audit sink and a per-fixture cost cap.
/// Real-LM tests instantiate their own OpenRouter client via the env-var key
/// (see <see cref="Skips.SkipUnlessRealLm"/>); the fixture is the offline default.
/// </summary>
public sealed class ParetoTestFixture : IDisposable
{
    /// <summary>Shared in-memory chat fake.</summary>
    public FakeChatClient Lm { get; } = new();

    /// <summary>Shared in-memory audit sink.</summary>
    public InMemoryAuditSink Audit { get; } = new();

    /// <summary>
    /// Hard cost ceiling for any single Pareto test invocation, in cents.
    /// Tests that route through a real LM MUST guard their request count
    /// against this cap.
    /// </summary>
    public const decimal MaxTestCostCents = 5m;

    /// <inheritdoc />
    public void Dispose() { }
}

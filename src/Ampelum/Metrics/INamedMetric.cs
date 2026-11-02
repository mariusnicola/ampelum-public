namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Opt-in capability for <see cref="IMetric"/> implementations that expose a stable string
/// identifier suitable for use as a registry key, report column header, or telemetry tag.
/// Adding the <see cref="Name"/> directly to <see cref="IMetric"/> would be a breaking change;
/// this interface preserves source-compat for every existing metric implementation.
/// </summary>
public interface INamedMetric : IMetric
{
    /// <summary>Stable, human-readable identifier (e.g. <c>"exact_match"</c>, <c>"bleu-4"</c>, <c>"ndcg@5"</c>).</summary>
    string Name { get; }
}

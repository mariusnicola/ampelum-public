namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Optimizer-internal untyped metric. Returns a score in [0, 1].
/// </summary>
public interface IMetric
{
    /// <summary>Score <paramref name="actual"/> against <paramref name="expected"/>. Returns [0,1].</summary>
    double Score(object actual, object expected, SignatureMetadata signature);
}

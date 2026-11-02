namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Untyped metric interface used by optimizer internals where the signature type
/// is not statically known. Typed metrics implement <see cref="IMetric{TSig}"/>
/// which provides a default interface member forwarding to the typed <c>Score</c>.
/// </summary>
public interface IMetric
{
    /// <summary>Returns a score in <c>[0.0, 1.0]</c>.</summary>
    double Score(object actual, object expected, SignatureMetadata signature);
}

/// <summary>
/// Typed metric. Implementations MUST be stateless and thread-safe â€” <c>Evaluate&lt;TSig&gt;</c>
/// calls <c>Score</c> concurrently under <c>MaxDegreeOfParallelism</c>.
/// </summary>
public interface IMetric<TSig> : IMetric where TSig : ISignature
{
    /// <summary>Returns a score in <c>[0.0, 1.0]</c>.</summary>
    double Score(TSig actual, TSig expected, SignatureMetadata signature);

    double IMetric.Score(object actual, object expected, SignatureMetadata signature) =>
        Score((TSig)actual, (TSig)expected, signature);
}

/// <summary>
/// Opt-in capability for <see cref="IMetric{TSig}"/> implementations that can produce a
/// human-readable feedback string describing why a prediction fell below threshold.
/// <c>Refine&lt;TSig&gt;</c> uses this string in its refinement prompt when available.
/// </summary>
public interface IRefinableFeedback
{
    /// <summary>Returns a concise feedback string used in the refinement prompt.</summary>
    string GetFeedback(object actual, object expected, SignatureMetadata signature);
}

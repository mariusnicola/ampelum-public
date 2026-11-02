namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Metric that delegates to a user Func&lt;TSig,TSig,double&gt;. Delegate MUST be thread-safe and return [0,1].
/// </summary>
public sealed class UserDefined<TSig>(Func<TSig, TSig, double> scoreFn) : IMetric<TSig>
    where TSig : ISignature
{
    readonly Func<TSig, TSig, double> _fn = scoreFn;

    /// <summary>
    /// Invokes the user-defined score function.
    /// </summary>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature) => _fn(actual, expected);
}

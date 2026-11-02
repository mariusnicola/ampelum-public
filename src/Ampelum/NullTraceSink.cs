namespace SignalGrid.Ampelum;

/// <summary>
/// A no-op <see cref="ITraceSink"/> that discards all trace records. Use this as the default
/// sink when observability is not required, to avoid unnecessary allocations.
/// </summary>
public sealed class NullTraceSink : ITraceSink
{
    /// <summary>The singleton instance; use this instead of constructing a new instance.</summary>
    public static readonly NullTraceSink Instance = new();
    private NullTraceSink() { }

    /// <inheritdoc/>
    public void Write(TraceRecord record) { }
}

namespace SignalGrid.Ampelum.Usage;

/// <summary>
/// Opt-in capability for <see cref="ITraceSink"/> implementations that support in-process
/// history inspection. <c>AmpelumContext.InspectHistory</c> checks for this interface via pattern match.
/// </summary>
public interface IInspectableSink : ITraceSink
{
    /// <summary>Returns the last <paramref name="n"/> records written to this sink, in chronological order
    /// (oldest first). If <paramref name="n"/> is &lt;= 0 returns an empty list; if fewer than
    /// <paramref name="n"/> records exist returns all available records.</summary>
    IReadOnlyList<TraceRecord> GetRecent(int n);
}

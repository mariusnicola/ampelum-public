namespace SignalGrid.Ampelum;

/// <summary>
/// Receives completed <see cref="TraceRecord"/> instances after every <c>ForwardAsync</c> call.
/// Implementations must be fast (synchronous, milliseconds); async I/O must be buffered internally.
/// </summary>
public interface ITraceSink
{
    /// <summary>Write a completed trace record. MUST be fast (milliseconds). Sinks that need
    /// async I/O MUST buffer internally â€” this call is on the forward path.</summary>
    void Write(TraceRecord record);
}

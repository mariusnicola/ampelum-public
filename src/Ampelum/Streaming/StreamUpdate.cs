namespace SignalGrid.Ampelum.Streaming;

/// <summary>
/// A single update emitted by <c>ProgramStreamer</c>. PartialText accumulates toward the final value (not a delta).
/// IsFinal is true on the terminal update and only then is FinalValue non-null.
/// </summary>
public sealed record StreamUpdate<TOut>(
    string FieldName,
    string PartialText,
    bool IsFinal,
    TOut? FinalValue);

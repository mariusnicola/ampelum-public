namespace SignalGrid.Ampelum;

/// <summary>Opaque immutable state threaded through streaming parse. Owned by the adapter;
/// callers never inspect its fields directly.</summary>
public sealed record StreamingParseState(
    string CurrentFieldName,
    string AccumulatedText,
    string PendingTail,
    IReadOnlyDictionary<string, object?> CompletedFields,
    bool IsComplete)
{
    /// <summary>Maximum characters held in <see cref="PendingTail"/> between chunks.</summary>
    public const int MaxPartialMarkerLength = 64;
}

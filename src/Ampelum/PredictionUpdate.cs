namespace SignalGrid.Ampelum;

/// <summary>
/// One incremental update from a streaming prediction operation. Accumulated-text model:
/// <see cref="PartialText"/> grows across updates for the current <see cref="FieldName"/>.
/// <see cref="Value"/> and <see cref="Trace"/> are populated only on the terminal update
/// (<see cref="IsFinal"/> = <see langword="true"/>).
/// </summary>
public sealed record PredictionUpdate<TSig>(
    string FieldName,
    string PartialText,
    bool IsFieldComplete,
    bool IsFinal,
    TSig? Value,
    TraceRecord? Trace) where TSig : ISignature;

namespace SignalGrid.Ampelum.Streaming;

/// <summary>
/// Filters a streaming program's output to updates for a single named field.
/// Implementations MUST tolerate calls from background threads.
/// </summary>
public interface IStreamListener<TField>
{
    /// <summary>Called for each partial update; on terminal update isFinal=true and finalValue non-null.</summary>
    void OnUpdate(string partialText, bool isFinal, TField? finalValue);
}

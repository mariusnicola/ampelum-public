using Microsoft.Extensions.AI;
namespace SignalGrid.Ampelum;

/// <summary>
/// Opt-in capability for adapters that can parse streaming chunks. <see cref="ChatAdapter"/>
/// implements this in v0.2; <see cref="JsonAdapter"/> defers to v0.2.1.
/// </summary>
public interface IStreamingAdapter : IAdapter
{
    /// <summary>Called once per <see cref="ChatResponseUpdate"/>. Fold pattern: returns a new state;
    /// the caller passes the returned state to the next invocation.</summary>
    StreamingParseState OnChunk(
        SignatureMetadata signature,
        StreamingParseState state,
        ChatResponseUpdate chunk);

    /// <summary>Called once after the stream ends. Flushes any pending tail, projects
    /// accumulated text onto the signature's output fields, and returns the parsed dictionary.
    /// Throws <see cref="Ampelum.Exceptions.AdapterParseException"/> on failure.</summary>
    IReadOnlyDictionary<string, object?> Finalize(
        SignatureMetadata signature,
        StreamingParseState state);
}

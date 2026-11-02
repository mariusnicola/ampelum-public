using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>
/// Core adapter contract: translates between a Ampelum signature's typed inputs/outputs and
/// the raw <see cref="ChatMessage"/> lists and <see cref="ChatResponse"/> objects exchanged with the LM.
/// </summary>
public interface IAdapter
{
    /// <summary>
    /// Formats the inputs and optional few-shot demos into the list of messages to send to the LM.
    /// Implementations MUST validate all supplied inputs and demo fields against the signature before returning.
    /// </summary>
    /// <param name="signature">Resolved metadata for the signature being executed.</param>
    /// <param name="inputs">Input field values for the current forward call.</param>
    /// <param name="demos">Optional few-shot demonstration examples to include in the prompt.</param>
    /// <returns>Ordered list of chat messages ready to be sent to the LM.</returns>
    IReadOnlyList<ChatMessage> Format(SignatureMetadata signature,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<Example> demos);

    /// <summary>
    /// Parses the LM's <see cref="ChatResponse"/> into a dictionary of output field values keyed by field name.
    /// </summary>
    /// <param name="signature">Resolved metadata for the signature being executed.</param>
    /// <param name="response">The raw response returned by the LM.</param>
    /// <returns>Dictionary mapping each output field name to its parsed value.</returns>
    IReadOnlyDictionary<string, object?> Parse(SignatureMetadata signature, ChatResponse response);
}

/// <summary>
/// Extended adapter capability for providers that support structured (JSON-schema-constrained) output.
/// Adapters implementing this interface can supply a <see cref="ChatResponseFormat"/> to constrain the LM response.
/// </summary>
public interface IStructuredOutputAdapter : IAdapter
{
    /// <summary>
    /// Builds (and caches) the <see cref="ChatResponseFormat"/> that constrains the LM to emit
    /// a JSON object matching the signature's output fields.
    /// </summary>
    /// <param name="signature">Resolved metadata for the signature being executed.</param>
    /// <returns>A <see cref="ChatResponseFormat"/> suitable for passing to <c>ChatOptions.ResponseFormat</c>.</returns>
    ChatResponseFormat BuildResponseFormat(SignatureMetadata signature);
}

/// <summary>
/// Extended adapter capability used by test infrastructure to produce synthetic LM responses.
/// Adapters implementing this interface allow <c>FakeChatClient</c> (from the <c>Ampelum.Testing</c> package) to
/// enqueue type-safe outputs without constructing raw wire text manually.
/// </summary>
public interface ITestableAdapter : IAdapter
{
    /// <summary>Symmetric inverse of <see cref="IAdapter.Parse"/>: given outputs, produce a response
    /// body that Parse would round-trip back to those outputs. Used exclusively by test doubles.</summary>
    ChatResponse FormatResponse(SignatureMetadata signature, IReadOnlyDictionary<string, object?> outputs);
}

using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>
/// Immutable snapshot of a single <c>ForwardAsync</c> call, written to the configured <see cref="ITraceSink"/>
/// after every invocation (including failed ones). Used for observability, debugging, and optimizer training.
/// </summary>
/// <param name="Id">Unique identifier for this trace, also set as the <c>ampelum.trace_id</c> OpenTelemetry Activity tag.</param>
/// <param name="ModuleName">Name of the module that produced this trace (e.g., <c>"Predict"</c> or <c>"ChainOfThought"</c>).</param>
/// <param name="SignatureType">CLR type of the signature that was executed.</param>
/// <param name="Inputs">Input field values passed to the forward call.</param>
/// <param name="Outputs">Output field values parsed from the LM response; empty on failure.</param>
/// <param name="PromptMessages">The exact list of messages sent to the LM.</param>
/// <param name="RawResponse">The raw text returned by the LM; empty string if the call failed before a response was received.</param>
/// <param name="Usage">Token usage reported by the provider; <see langword="null"/> if unavailable.</param>
/// <param name="Duration">Wall-clock duration of the entire <c>ForwardAsync</c> call.</param>
/// <param name="Error">The exception that terminated the call, or <see langword="null"/> on success.</param>
/// <param name="Metadata">Arbitrary key-value pairs attached by the module (e.g., <c>"reasoning"</c> from ChainOfThought).</param>
public sealed record TraceRecord(
    Guid Id,
    string ModuleName,
    Type SignatureType,
    IReadOnlyDictionary<string, object?> Inputs,
    IReadOnlyDictionary<string, object?> Outputs,
    IReadOnlyList<ChatMessage> PromptMessages,
    string RawResponse,
    UsageDetails? Usage,
    TimeSpan Duration,
    Exception? Error,
    IReadOnlyDictionary<string, object?> Metadata);

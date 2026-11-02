using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>
/// The result of a successful <c>ForwardAsync</c> call: the strongly-typed output value paired
/// with the full <see cref="TraceRecord"/> for observability and optimizer use.
/// </summary>
/// <typeparam name="TValue">The signature type whose output properties are populated on <see cref="Value"/>.</typeparam>
/// <param name="Value">The signature record with all output properties populated from the LM response.</param>
/// <param name="Trace">Full trace of the forward call, including prompt, raw response, usage, and timing.</param>
public sealed record Prediction<TValue>(TValue Value, TraceRecord Trace);

/// <summary>Convenience extension methods for <see cref="Prediction{TValue}"/>.</summary>
public static class PredictionExtensions
{
    /// <summary>Returns the wall-clock duration of the forward call.</summary>
    public static TimeSpan Duration<T>(this Prediction<T> p) => p.Trace.Duration;

    /// <summary>Returns the token usage reported by the LM provider, or <see langword="null"/> if unavailable.</summary>
    public static UsageDetails? Usage<T>(this Prediction<T> p) => p.Trace.Usage;

    /// <summary>
    /// Returns the chain-of-thought reasoning text injected by <c>ChainOfThought</c>, or an empty string
    /// if no reasoning field is present in the trace outputs.
    /// </summary>
    public static string Reasoning<T>(this Prediction<T> p) =>
        p.Trace.Outputs.TryGetValue(TraceKeys.Reasoning, out var v) ? v?.ToString() ?? "" : "";
}

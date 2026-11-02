namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown when <see cref="AmpelumAssert.Suggest"/> is called with a false condition.
/// Caught by <c>Retry.RetryModule&lt;TOut&gt;</c>, which injects <see cref="Suggestion"/> into
/// the next attempt's system instructions.
/// </summary>
public sealed class SuggestionException(string suggestion) : AmpelumException(suggestion)
{
    /// <summary>The suggestion feedback to inject into the next retry attempt.</summary>
    public string Suggestion { get; } = suggestion;
}

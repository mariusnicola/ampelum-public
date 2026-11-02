namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown when an <see cref="Ampelum.IAdapter"/> cannot parse the LM response into the expected output fields.
/// Includes the raw response text (truncated) and a human-readable <see cref="Hint"/> to help diagnose the failure.
/// </summary>
public sealed class AdapterParseException : AmpelumException
{
    const int PreviewLen = 500;

    /// <summary>Initializes a new <see cref="AdapterParseException"/> with context about the failed parse.</summary>
    /// <param name="signatureName">Name of the signature whose output was being parsed.</param>
    /// <param name="adapterName">Name of the adapter that attempted the parse.</param>
    /// <param name="rawResponse">The raw LM response text (may be truncated in the message).</param>
    /// <param name="hint">A short, actionable description of what went wrong.</param>
    public AdapterParseException(string signatureName, string adapterName, string rawResponse, string hint)
        : base(BuildMessage(signatureName, adapterName, rawResponse, hint))
    {
        SignatureName = signatureName;
        AdapterName = adapterName;
        RawResponse = rawResponse;
        Hint = hint;
    }

    /// <summary>Name of the signature whose response could not be parsed.</summary>
    public string SignatureName { get; }

    /// <summary>Name of the adapter that attempted (and failed) the parse.</summary>
    public string AdapterName { get; }

    /// <summary>The full raw response text returned by the LM.</summary>
    public string RawResponse { get; }

    /// <summary>Short, actionable description of what went wrong and how to fix it.</summary>
    public string Hint { get; }

    static string BuildMessage(string sig, string adapter, string raw, string hint)
    {
        var preview = raw.Length <= PreviewLen ? raw : raw[..PreviewLen] + "â€¦";
        return $"Failed to parse response for signature '{sig}' using {adapter}. " +
               $"Raw response (first {PreviewLen} chars): \"{preview}\". Hint: {hint}";
    }
}

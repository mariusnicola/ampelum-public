using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Pii;

/// <summary>Strategy for redacting PII from arbitrary strings or audit-shaped dictionaries.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IPiiRedactor
{
    /// <summary>Returns a copy of <paramref name="text"/> with every matched PII substring replaced.</summary>
    string Redact(string text);

    /// <summary>Returns a shallow copy of <paramref name="source"/> with string-typed values redacted.
    /// Non-string values pass through unchanged. The returned dictionary is independent of the input.</summary>
    IReadOnlyDictionary<string, object?> RedactDictionary(IReadOnlyDictionary<string, object?> source);
}

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Tracing;

/// <summary>Resolves a W3C TraceContext-compliant 32-lowercase-hex trace id per HQ-1.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class TraceIdResolver
{
    /// <summary>
    /// Returns the trace id to use for this request.
    /// Priority: valid <paramref name="requestParentTraceId"/> → Activity.Current's trace id → new random id.
    /// </summary>
    public static string Resolve(string? requestParentTraceId = null)
    {
        if (!string.IsNullOrEmpty(requestParentTraceId)
            && requestParentTraceId.Length == 32
            && IsLowercaseHex(requestParentTraceId))
            return requestParentTraceId;

        var current = Activity.Current?.TraceId;
        if (current is { } tid && tid != default)
        {
            var hex = tid.ToHexString();
            if (hex != "00000000000000000000000000000000")
                return hex;
        }
        return ActivityTraceId.CreateRandom().ToHexString();
    }

    static bool IsLowercaseHex(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }
}

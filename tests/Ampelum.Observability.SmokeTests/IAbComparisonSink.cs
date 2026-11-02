using System.Diagnostics.CodeAnalysis;

namespace Ampelum.Observability.SmokeTests;

/// <summary>
/// Forward-defined marker for v0.9d's <c>EvalComparisonReport</c> sink hook.
/// Once v0.9d ships, the smoke matrix asserts that A/B comparison reports
/// surface in the wired observability backends (LangSmith, LangFuse, Phoenix,
/// Application Insights).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IAbComparisonSink
{
    /// <summary>Emit an A/B comparison report payload to the sink.</summary>
    void Write(string reportName, IReadOnlyDictionary<string, object?> body);
}

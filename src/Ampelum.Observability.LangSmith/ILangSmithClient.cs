using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>HTTP-boundary contract for the LangSmith /runs ingestion API.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface ILangSmithClient
{
    /// <summary>Submits a batch of runs. Implementations must redact credentials from any thrown exception.</summary>
    Task SubmitAsync(IReadOnlyList<LangSmithRun> runs, CancellationToken cancellationToken);
}

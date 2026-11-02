using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>HTTP-boundary contract for the LangFuse <c>/api/public/ingestion</c> endpoint.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface ILangFuseClient
{
    /// <summary>Submits a batch of events.</summary>
    Task SubmitAsync(IReadOnlyList<LangFuseEvent> events, CancellationToken cancellationToken);
}

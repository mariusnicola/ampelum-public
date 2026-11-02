using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host;

/// <summary>Server-sent event envelope.</summary>
public sealed record SseEnvelope(string EventName, string JsonPayload, long? Id);

/// <summary>Subscribes to optimizer progress events for a specific run.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IOptimizerProgressBus
{
    /// <summary>Publishes a progress envelope to all active subscribers for the given run.</summary>
    void Publish(string runId, SseEnvelope envelope);

    /// <summary>Signals that the run has completed; subscribers will drain and exit.</summary>
    void Complete(string runId);

    /// <summary>Subscribes to progress events for the given run ID.</summary>
    IAsyncEnumerable<SseEnvelope> Subscribe(string runId, CancellationToken ct);
}

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Batch;

/// <summary>
/// Provider-agnostic asynchronous batch chat surface (PSQ-4, v0.9g).
/// Distinct from <see cref="IChatClient"/> because provider batch APIs
/// (OpenAI Batch, Anthropic Messages Batch) are fire-and-poll with hour-scale
/// latency in exchange for ~50% cost savings.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IBatchChatClient
{
    /// <summary>Submit a batch of chat requests; returns a handle for status polling and result retrieval.</summary>
    /// <param name="requests">Batch input items, each with a unique <see cref="BatchRequest.CustomId"/>.</param>
    /// <param name="options">Optional default chat options applied when an item omits its own.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<BatchHandle> SubmitAsync(
        IReadOnlyList<BatchRequest> requests,
        ChatOptions? options,
        CancellationToken cancellationToken);

    /// <summary>Fetch the current status of a previously submitted batch.</summary>
    Task<BatchStatus> GetStatusAsync(BatchHandle handle, CancellationToken cancellationToken);

    /// <summary>Stream completed results as they become available.</summary>
    IAsyncEnumerable<BatchResultItem> StreamResultsAsync(BatchHandle handle, CancellationToken cancellationToken);

    /// <summary>Cancel a batch that is still in progress.</summary>
    Task CancelAsync(BatchHandle handle, CancellationToken cancellationToken);
}

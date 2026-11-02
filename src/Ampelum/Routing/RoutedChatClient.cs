using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Routing;

/// <summary>
/// <see cref="IChatClient"/> decorator that consults an <see cref="IRoutingStrategy"/>
/// to pick the primary <see cref="ProviderEndpoint"/> per call and escalates down the
/// returned fallback chain on transport failure (PSQ-1, v0.9g).
/// </summary>
/// <remarks>
/// <para>The router is intentionally allocation-light: all endpoint state is held in
/// an <see cref="IReadOnlyList{T}"/> supplied at construction; per-call work is the
/// strategy lookup plus, on failure, walking the fallback chain.</para>
/// <para>Streaming round-trips do NOT participate in failover for this batch (a partial
/// stream cannot safely be retried mid-flight); they always run on the primary.</para>
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RoutedChatClient : IChatClient
{
    readonly IReadOnlyList<ProviderEndpoint> _endpoints;
    readonly IRoutingStrategy _strategy;
    readonly ChatClientMetadata _metadata;

    /// <summary>Creates a new <see cref="RoutedChatClient"/>.</summary>
    /// <param name="endpoints">One-or-more provider endpoints. The list is captured by reference;
    /// callers should treat it as immutable after construction.</param>
    /// <param name="strategy">Pluggable selection strategy (e.g. <see cref="Failover"/>,
    /// <see cref="LoadBalanced"/>, <see cref="KeyRotating"/>).</param>
    public RoutedChatClient(IReadOnlyList<ProviderEndpoint> endpoints, IRoutingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(strategy);
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
        _endpoints = endpoints;
        _strategy = strategy;
        _metadata = new ChatClientMetadata(nameof(RoutedChatClient));
    }

    /// <inheritdoc cref="IChatClient"/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var decision = _strategy.Select(_endpoints);
        var attempts = new List<ProviderEndpoint>(1 + decision.Fallbacks.Count) { decision.Primary };
        attempts.AddRange(decision.Fallbacks);

        // Buffer the messages so a retry on a fallback endpoint can re-enumerate them.
        var buffered = messages as IList<ChatMessage> ?? [.. messages];

        for (var i = 0; ; i++)
        {
            var endpoint = attempts[i];
            try
            {
                var response = await endpoint.Client
                    .GetResponseAsync(buffered, options, cancellationToken)
                    .ConfigureAwait(false);
                Stamp(response, endpoint);
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (i < attempts.Count - 1)
            {
                // Swallow and try the next endpoint in the fallback chain.
            }
        }
    }

    /// <inheritdoc cref="IChatClient"/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var decision = _strategy.Select(_endpoints);
        return decision.Primary.Client.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc cref="IChatClient"/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceType == typeof(ChatClientMetadata)) return _metadata;
        if (serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    /// <inheritdoc cref="IDisposable"/>
    public void Dispose()
    {
        // Endpoints are owned by the caller / DI container; the router does not dispose them.
    }

    static void Stamp(ChatResponse response, ProviderEndpoint endpoint)
    {
        var props = response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        props[HookMetadataKeys.RouterTargetName] = endpoint.Name;
    }
}

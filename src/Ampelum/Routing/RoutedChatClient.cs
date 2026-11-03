using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
/// <para>A streamed call fails over only while no update has been returned yet: an endpoint whose stream
/// throws or ends before its first update is replaced by the next one in the chain. Once an update was returned,
/// a later failure reaches the caller, because a partial stream cannot safely be retried.</para>
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
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var decision = _strategy.Select(_endpoints);
        var attempts = new List<ProviderEndpoint>(1 + decision.Fallbacks.Count) { decision.Primary };
        attempts.AddRange(decision.Fallbacks);
        var buffered = messages as IList<ChatMessage> ?? [.. messages];

        for (var i = 0; ; i++)
        {
            IAsyncEnumerator<ChatResponseUpdate>? stream = null;
            var hasFirst = false;
            Exception? failure = null;
            try
            {
                stream = attempts[i].Client.GetStreamingResponseAsync(buffered, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                hasFirst = await stream.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is not null || !hasFirst)
            {
                if (stream is not null)
                {
                    // A stream that cannot be closed must not stop the failover or hide the error being handled.
                    try { await stream.DisposeAsync().ConfigureAwait(false); }
                    catch { /* see above */ }
                }
                var isLast = i == attempts.Count - 1;
                if (failure is not null)
                {
                    // Nothing was sent yet, so the next endpoint can answer instead.
                    if (isLast || (failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
                        ExceptionDispatchInfo.Capture(failure).Throw();
                    continue;
                }
                // A stream that ended before its first update counts as a failure, except on the last endpoint,
                // whose empty stream is the answer.
                if (!isLast) continue;
                yield break;
            }

            // From the first update on the caller has data, so a later failure is not retried.
            try
            {
                yield return stream!.Current;
                while (await stream.MoveNextAsync().ConfigureAwait(false))
                    yield return stream.Current;
            }
            finally
            {
                await stream!.DisposeAsync().ConfigureAwait(false);
            }
            yield break;
        }
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

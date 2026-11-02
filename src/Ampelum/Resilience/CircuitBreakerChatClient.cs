using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Polly;
using Polly.CircuitBreaker;

namespace SignalGrid.Ampelum.Resilience;

/// <summary>
/// <see cref="IChatClient"/> decorator that fronts the inner client with a Polly v8
/// circuit breaker (v0.9g). Trips on a configurable predicate (default: 5xx + provider-side
/// rate-limit exhaustion) and rejects subsequent calls during the break window with a
/// <see cref="BrokenCircuitException"/> until the half-open probe succeeds.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CircuitBreakerChatClient : IChatClient
{
    readonly IChatClient _inner;
    readonly ResiliencePipeline _pipeline;
    readonly ChatClientMetadata _metadata;

    /// <summary>Creates a new <see cref="CircuitBreakerChatClient"/>.</summary>
    /// <param name="inner">The wrapped client.</param>
    /// <param name="options">Breaker configuration.</param>
    public CircuitBreakerChatClient(IChatClient inner, CircuitBreakerOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _metadata = new ChatClientMetadata(nameof(CircuitBreakerChatClient));

        var trip = options.ShouldTrip;
        var threshold = Math.Max(2, options.FailureThreshold);
        // Polly v8 clamps BreakDuration to [500ms, 24h] and SamplingDuration to [500ms, 24h] /
        // requires SamplingDuration >= BreakDuration on construction. Apply defensive clamps so
        // user-supplied TimeSpans never trip Polly's validator.
        var breakDuration = options.BreakDuration < TimeSpan.FromMilliseconds(500)
            ? TimeSpan.FromMilliseconds(500)
            : options.BreakDuration > TimeSpan.FromDays(1)
                ? TimeSpan.FromDays(1)
                : options.BreakDuration;
        var samplingDuration = breakDuration;

        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = threshold,
                SamplingDuration = samplingDuration,
                BreakDuration = breakDuration,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } ex && trip(ex)),
            })
            .Build();
    }

    /// <inheritdoc cref="IChatClient"/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _pipeline.ExecuteAsync(
            static async (state, ct) => await state.client._inner.GetResponseAsync(state.messages, state.options, ct).ConfigureAwait(false),
            (client: this, messages, options),
            cancellationToken).AsTask();
    }

    /// <inheritdoc cref="IChatClient"/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Initiate the stream through the pipeline so circuit-open is observed before the
        // first yield. Once the stream is hot, downstream errors flow through unfiltered.
        var enumerable = await _pipeline.ExecuteAsync(
            static (state, _) =>
            {
                var stream = state.client._inner.GetStreamingResponseAsync(state.messages, state.options, state.ct);
                return ValueTask.FromResult(stream);
            },
            (client: this, messages, options, ct: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await foreach (var update in enumerable.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc cref="IChatClient"/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceType == typeof(ChatClientMetadata)) return _metadata;
        if (serviceType.IsInstanceOfType(this)) return this;
        return _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc cref="IDisposable"/>
    public void Dispose()
    {
        // Pipeline is owned and disposable.
        _pipeline.GetType();
    }
}

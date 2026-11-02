using System.Diagnostics.CodeAnalysis;
using System.Threading.RateLimiting;
using SignalGrid.Ampelum.Exceptions;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.RateLimiting;

/// <summary>
/// <see cref="IChatClient"/> decorator that enforces a per-partition token-bucket
/// rate limit before forwarding each call to the inner chat client (PSQ-3, v0.9g).
/// </summary>
/// <remarks>
/// <para>The partition key is extracted from <see cref="ChatOptions.AdditionalProperties"/>
/// using <c>HookMetadataKeys.BudgetScope</c>, falling back to <c>HookMetadataKeys.RouterTargetName</c>,
/// and finally to <c>"default"</c>. Buckets are lazily allocated per partition, sized
/// <c>RequestsPerMinute * BurstMultiplier</c>.</para>
/// <para>Streaming calls share the same bucket; one permit is acquired per stream
/// initiation regardless of chunk count.</para>
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RateLimitedChatClient : IChatClient
{
    readonly IChatClient _inner;
    readonly RateLimitingOptions _options;
    readonly PartitionedRateLimiter<string> _limiter;
    readonly ChatClientMetadata _metadata;

    /// <summary>Creates a new <see cref="RateLimitedChatClient"/>.</summary>
    /// <param name="inner">The wrapped client.</param>
    /// <param name="options">Bucket configuration.</param>
    public RateLimitedChatClient(IChatClient inner, RateLimitingOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestsPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "RequestsPerMinute must be positive.");
        if (options.BurstMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "BurstMultiplier must be positive.");
        _inner = inner;
        _options = options;
        _metadata = new ChatClientMetadata(nameof(RateLimitedChatClient));

        var rpm = options.RequestsPerMinute;
        var bucketSize = (int)Math.Ceiling(rpm * options.BurstMultiplier);
        _limiter = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = bucketSize,
                TokensPerPeriod = rpm,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = bucketSize,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));
    }

    /// <inheritdoc cref="IChatClient"/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var key = ResolveKey(options);
        await AcquireOrThrowAsync(key, cancellationToken).ConfigureAwait(false);
        return await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IChatClient"/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = ResolveKey(options);
        await AcquireOrThrowAsync(key, cancellationToken).ConfigureAwait(false);
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
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
        _limiter.Dispose();
    }

    static string ResolveKey(ChatOptions? options)
    {
        if (options?.AdditionalProperties is { } props)
        {
            if (props.TryGetValue(HookMetadataKeys.BudgetScope, out var scope) && scope is string s && !string.IsNullOrEmpty(s))
                return s;
            if (props.TryGetValue(HookMetadataKeys.RouterTargetName, out var target) && target is string t && !string.IsNullOrEmpty(t))
                return t;
        }
        return "default";
    }

    async ValueTask AcquireOrThrowAsync(string key, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.AcquireTimeout);
        RateLimitLease lease;
        try
        {
            lease = await _limiter.AcquireAsync(key, 1, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RateLimitedException(key, _options.AcquireTimeout);
        }
        lease.Dispose();
    }
}

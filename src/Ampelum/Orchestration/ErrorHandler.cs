using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>Retry-and-fallback wrapper for any <see cref="IModule{TSig,TSig}"/>.
/// On failure, retries up to <see cref="ErrorHandlerConfig.MaxRetries"/> times (with
/// configurable delay and retryability filter), then falls back to a default output
/// if configured, or rethrows the last exception.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ErrorHandler<TSig> : IModule<TSig, TSig> where TSig : ISignature
{
    readonly IModule<TSig, TSig> _inner;
    readonly ErrorHandlerConfig _config;

    /// <summary>Creates an ErrorHandler wrapping <paramref name="inner"/> with the given <paramref name="config"/>.</summary>
    public ErrorHandler(IModule<TSig, TSig> inner, ErrorHandlerConfig config)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(config.MaxRetries), "Must be >= 0.");
    }

    /// <inheritdoc/>
    public async Task<Prediction<TSig>> ForwardAsync(TSig inputs, CancellationToken ct = default)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                return await _inner.ForwardAsync(inputs, ct);
            }
            catch (Exception ex) when (_config.IsRetryable?.Invoke(ex) ?? true)
            {
                last = ex;
                if (attempt < _config.MaxRetries)
                {
                    _config.OnRetry?.Invoke(ex, attempt + 1);
                    if (_config.RetryDelay > TimeSpan.Zero)
                        await Task.Delay(_config.RetryDelay, ct);
                }
            }
        }

        if (_config.FallbackOutput is not null)
        {
            var fallbackTrace = new TraceRecord(
                Id: Guid.NewGuid(),
                ModuleName: $"ErrorHandler<{typeof(TSig).Name}>",
                SignatureType: typeof(TSig),
                Inputs: new Dictionary<string, object?>(),
                Outputs: new Dictionary<string, object?>(),
                PromptMessages: Array.Empty<ChatMessage>(),
                RawResponse: string.Empty,
                Usage: null,
                Duration: TimeSpan.Zero,
                Error: last,
                Metadata: new Dictionary<string, object?>());
            return new Prediction<TSig>(_config.FallbackOutput(inputs), fallbackTrace);
        }

        throw new OperationCanceledException(
            $"ErrorHandler exhausted {_config.MaxRetries} retries for {typeof(TSig).Name}.", last);
    }

    /// <summary>Configuration for <see cref="ErrorHandler{TSig}"/>.</summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public sealed class ErrorHandlerConfig
    {
        /// <summary>Maximum number of retries (default 1). Total attempts = MaxRetries + 1.</summary>
        public int MaxRetries { get; init; } = 1;

        /// <summary>Delay between retries (default 100ms).</summary>
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

        /// <summary>Optional predicate — only exceptions matching this are retried.
        /// When null, all exceptions are retried.</summary>
        public Func<Exception, bool>? IsRetryable { get; init; }

        /// <summary>Called after each retry with the exception and attempt number (1-based).</summary>
        public Action<Exception, int>? OnRetry { get; init; }

        /// <summary>Factory producing a fallback TSig value when all retries are exhausted.
        /// When null, the last exception is rethrown wrapped in <see cref="OperationCanceledException"/>.</summary>
        public Func<TSig, TSig>? FallbackOutput { get; init; }
    }
}
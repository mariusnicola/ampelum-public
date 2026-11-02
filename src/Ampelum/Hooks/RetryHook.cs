using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum.Hooks;

/// <summary>
/// Hook (Order=100) that handles transient and schema-violation failures. v0.9c upgrades
/// the v0.6a metadata-only stub to active schema-recovery: when <see cref="AdapterParseException"/>
/// is observed, the hook prepares a corrective re-prompt (stored in metadata) and increments
/// the recovery-attempt counter. Other exceptions follow the existing exponential-back-off
/// metadata path. Stateless: counters live in <see cref="HookContext.Metadata"/>.
/// </summary>
public sealed class RetryHook : IHook
{
    /// <summary>Hard ceiling for max recovery attempts regardless of constructor argument.</summary>
    internal const int RecoveryAttemptsCeiling = 5;

    readonly int _maxAttempts;
    readonly int _maxRecoveryAttempts;
    readonly TimeSpan _baseDelay;
    readonly TimeSpan _maxDelay;
    readonly Random _rng;

    /// <summary>Builds a retry hook. <paramref name="maxRecoveryAttempts"/> bounds schema-recovery
    /// re-prompts; capped at <see cref="RecoveryAttemptsCeiling"/>.</summary>
    public RetryHook(
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Random? rng = null,
        int maxRecoveryAttempts = 2)
    {
        _maxAttempts = maxAttempts;
        _maxRecoveryAttempts = Math.Min(maxRecoveryAttempts, RecoveryAttemptsCeiling);
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(200);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
        _rng = rng ?? Random.Shared;
    }

    /// <inheritdoc/>
    public int Order => 100;

    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;

    /// <inheritdoc/>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
    {
        if (error is AdapterParseException parseError)
        {
            int prior = ctx.Metadata.TryGetValue("retry.recovery_attempt", out var rv) && rv is int rn ? rn : 0;
            int next = prior + 1;
            ctx.Set("retry.recovery_attempt", next);
            ctx.Set("retry.max_recovery_attempts", _maxRecoveryAttempts);
            if (next > _maxRecoveryAttempts)
            {
                ctx.Set("retry.recovered", false);
                return default;
            }

            string failingResponse = parseError.RawResponse;
            string corrective =
                $"Your previous response could not be parsed: {parseError.Hint}\n" +
                "The malformed response was:\n---\n" +
                $"{(string.IsNullOrEmpty(failingResponse) ? "<unavailable>" : failingResponse)}\n---\n" +
                "Please re-emit the response strictly conforming to the field schema. " +
                "Output only the structured fields; do not apologise or explain.";
            ctx.Set("retry.corrective_prompt", corrective);
            ctx.Set("retry.recovered", true);
            return default;
        }

        int priorAttempt = ctx.Metadata.TryGetValue("retry.attempt", out var v) && v is int n ? n : 0;
        int attempt = priorAttempt + 1;
        ctx.Set("retry.attempt", attempt);
        ctx.Set("retry.max_attempts", _maxAttempts);

        long baseMs = (long)_baseDelay.TotalMilliseconds;
        long maxMs = (long)_maxDelay.TotalMilliseconds;
        long shifted = baseMs;
        for (int i = 1; i < attempt && shifted < maxMs; i++) shifted *= 2;
        if (shifted > maxMs) shifted = maxMs;
        int jitter = _rng.Next(0, (int)Math.Max(1, baseMs));
        int total = (int)Math.Min(int.MaxValue, shifted + jitter);
        ctx.Set("retry.next_delay_ms", total);
        return default;
    }
}

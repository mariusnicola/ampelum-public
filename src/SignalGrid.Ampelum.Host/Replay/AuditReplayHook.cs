#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Host.Replay;

/// <summary>
/// Hook that short-circuits the LM call for audit replay (spec §11).
/// Order = -1000 ensures it precedes BudgetHook (Order=0).
/// HQ-2: if a hook with lower Order needs to run before replay, revisit.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AuditReplayHook : IHook
{
    readonly AuditEntry _entry;
    readonly string _expectedReplayHash;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditReplayHook"/> class.
    /// </summary>
    /// <param name="entry">The audit entry to replay.</param>
    /// <param name="expectedReplayHash">The expected replay hash computed from the entry's inputs.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="expectedReplayHash"/> is null or empty.</exception>
    public AuditReplayHook(AuditEntry entry, string expectedReplayHash)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(expectedReplayHash);
        _entry = entry;
        _expectedReplayHash = expectedReplayHash;
    }

    /// <summary>
    /// Gets the hook execution order. Lower values run first in OnBefore.
    /// </summary>
    public int Order => -1000;

    /// <summary>
    /// Validates the replay hash and sets outputs to short-circuit the LM call.
    /// </summary>
    /// <param name="ctx">The hook context containing inputs and where outputs will be set.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the replay hash does not match the expected hash.</exception>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        var actual = AuditChainHasher.ComputeReplay(
            _entry.ModuleName, _entry.EventType, _entry.ModelId, ctx.Inputs);
        if (!string.Equals(actual, _expectedReplayHash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Replay hash mismatch: expected {_expectedReplayHash}, got {actual}. " +
                "Audit row may belong to a renamed signature.");
        ctx.Outputs = _entry.Outputs;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Verifies that no LM call occurred during the replay.
    /// </summary>
    /// <param name="ctx">The hook context containing token counts.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ReplayLmCalledException">Thrown when prompt or completion tokens are non-zero.</exception>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        if ((ctx.PromptTokens ?? 0) != 0 || (ctx.CompletionTokens ?? 0) != 0)
            throw new ReplayLmCalledException(
                $"AuditReplayHook short-circuit failed: LM was called " +
                $"(prompt={ctx.PromptTokens}, completion={ctx.CompletionTokens}).");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles errors by doing nothing. Errors from the LM call are not expected during replay.
    /// </summary>
    /// <param name="ctx">The hook context.</param>
    /// <param name="error">The exception that occurred.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) =>
        ValueTask.CompletedTask;
}

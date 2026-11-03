using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace SignalGrid.Ampelum.Hooks.Internal;

/// <summary>
/// Internal helper that owns the pre-sorted hook list and runs OnBefore (ascending),
/// OnAfter (descending), and OnError (descending, exceptions swallowed).
/// </summary>
/// <remarks>
/// v1.0-rc1 alpha.6 P2: instances are cached per <c>IReadOnlyList&lt;IHook&gt;</c>
/// reference via <see cref="GetOrCreate"/> â€” Predict's hook list is fixed on the
/// AmpelumContext (immutable record set via <c>WithHook</c>), so the sorted ascending
/// + descending arrays do not need to be rebuilt on every <c>ForwardAsync</c>.
/// </remarks>
internal sealed class HookPipeline
{
    static readonly ConditionalWeakTable<IReadOnlyList<IHook>, HookPipeline> _cache = new();

    readonly IHook[] _ascending;
    readonly IHook[] _descending;

    HookPipeline(IReadOnlyList<IHook> hooks)
    {
        var sorted = hooks.Select((h, i) => (Hook: h, Index: i))
                          .OrderBy(t => t.Hook.Order).ThenBy(t => t.Index)
                          .Select(t => t.Hook).ToArray();
        _ascending = sorted;
        var reversed = new IHook[sorted.Length];
        for (var i = 0; i < sorted.Length; i++) reversed[i] = sorted[sorted.Length - 1 - i];
        _descending = reversed;
    }

    /// <summary>
    /// Returns a cached pipeline for <paramref name="hooks"/>, building one on first call.
    /// Cache is keyed by reference identity of the list, which is stable across <c>ForwardAsync</c>
    /// calls that share the same <see cref="AmpelumContext"/>.
    /// </summary>
    public static HookPipeline GetOrCreate(IReadOnlyList<IHook> hooks)
        => _cache.GetValue(hooks, static h => new HookPipeline(h));

    public async ValueTask RunBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        for (var i = 0; i < _ascending.Length; i++)
        {
            await _ascending[i].OnBeforeAsync(ctx, ct).ConfigureAwait(false);
            if (ctx.Outputs is not null) break;
        }
    }

    /// <summary>
    /// Runs every after hook in descending order even when one throws, so a lower-order hook such as the
    /// audit hook still sees the call. One failure is rethrown as it is; several are rethrown together
    /// as an <see cref="AggregateException"/> in the order they happened.
    /// </summary>
    public async ValueTask RunAfterAsync(HookContext ctx, CancellationToken ct)
    {
        List<Exception>? failures = null;
        for (var i = 0; i < _descending.Length; i++)
        {
            try { await _descending[i].OnAfterAsync(ctx, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add(ex);
                // Tells the hooks still to run (the semantic cache) that this call's outputs must not be kept.
                ctx.Set(HookMetadataKeys.AfterHookFailed, true);
            }
        }
        if (failures is null) return;
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    public async ValueTask RunErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
    {
        for (var i = 0; i < _descending.Length; i++)
        {
            try { await _descending[i].OnErrorAsync(ctx, error, ct).ConfigureAwait(false); }
            catch { /* must-not-throw rule */ }
        }
    }
}

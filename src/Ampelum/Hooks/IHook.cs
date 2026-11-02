using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Hooks;

/// <summary>
/// Cross-cutting concern that runs before, after, and on error for every module forward pass.
/// Implementations must be stateless or thread-safe â€” the same instance is shared across
/// all parallel ForwardAsync calls that resolve the same AmpelumContext.
/// </summary>
public interface IHook
{
    /// <summary>Lower Order runs first in OnBefore (ascending) and last in OnAfter/OnError (descending).</summary>
    int Order { get; }

    /// <summary>Runs before the LM call. Throwing aborts subsequent hooks AND the LM call.</summary>
    ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct);

    /// <summary>Runs after a successful LM call + parse. ctx.Outputs is non-null here.</summary>
    ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct);

    /// <summary>Runs when the LM call, parse, or any hook throws. MUST NOT throw.</summary>
    ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct);
}

/// <summary>
/// Per-call context passed to every hook in the pipeline. Mutable Metadata (thread-safe)
/// lets hooks coordinate (e.g. BudgetHook reads HookMetadataKeys.BudgetScope and BudgetCostCents).
/// </summary>
/// <remarks>
/// v1.0-rc1 alpha.6 P2: the four well-known LM-result keys
/// (<see cref="HookMetadataKeys.ModelId"/>, <see cref="HookMetadataKeys.PromptTokens"/>,
/// <see cref="HookMetadataKeys.CompletionTokens"/>, <see cref="HookMetadataKeys.LmCallDurationMs"/>)
/// are stored in strongly-typed nullable backing fields, eliminating the boxing of
/// <see langword="long"/>/<see langword="double"/> that the previous
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/> backing imposed.
/// All other keys go to a lazily-initialised <see cref="Dictionary{TKey, TValue}"/> guarded by
/// a per-instance lock to preserve the documented thread-safety contract for cross-hook
/// metadata writes (PSQ-3). The <see cref="Metadata"/> projection unifies typed slots and the
/// extras dictionary; consumers are unchanged.
/// </remarks>
public sealed class HookContext : IReadOnlyDictionary<string, object?>
{
    // Lock guards _extras AND the four typed slots below for the cross-thread Set contract
    // exercised by HookContextTests.Set_IsThreadSafe. Within a single Predict.ForwardAsync call,
    // hooks run sequentially so this lock is uncontended on the hot path.
    readonly object _gate = new();
    Dictionary<string, object?>? _extras;

    string? _modelId;
    long? _promptTokens;
    long? _completionTokens;
    double? _lmCallDurationMs;

    /// <summary>The module name that triggered this pipeline (e.g. "Predict", "ChainOfThought").</summary>
    public required string ModuleName { get; init; }

    /// <summary>Read-only snapshot of input field values projected from the signature record.</summary>
    public required IReadOnlyDictionary<string, object?> Inputs { get; init; }

    /// <summary>Parsed outputs. Null until either (a) the pipeline has run <c>Adapter.Parse</c>
    /// successfully, or (b) an <c>OnBefore</c> hook has written this property to trigger the PSQ-11
    /// short-circuit (replay/cache hit). Public setter â€” see PSQ-8.</summary>
    public IReadOnlyDictionary<string, object?>? Outputs { get; set; }

    /// <summary>Metadata bag visible to all hooks in this call. Use <see cref="Set"/> to write.</summary>
    public IReadOnlyDictionary<string, object?> Metadata => this;

    /// <summary>
    /// Strongly-typed value for <see cref="HookMetadataKeys.ModelId"/>. Reads and writes the same
    /// slot exposed via <see cref="Metadata"/> indexer; the property exists so hot-path callers can
    /// avoid the <see cref="object"/>-keyed lookup. Net-new in v1.0-rc1 alpha.6.
    /// </summary>
    public string? ModelId
    {
        get { lock (_gate) return _modelId; }
        set { lock (_gate) _modelId = value; }
    }

    /// <summary>
    /// Strongly-typed value for <see cref="HookMetadataKeys.PromptTokens"/>. Net-new in v1.0-rc1 alpha.6.
    /// </summary>
    public long? PromptTokens
    {
        get { lock (_gate) return _promptTokens; }
        set { lock (_gate) _promptTokens = value; }
    }

    /// <summary>
    /// Strongly-typed value for <see cref="HookMetadataKeys.CompletionTokens"/>. Net-new in v1.0-rc1 alpha.6.
    /// </summary>
    public long? CompletionTokens
    {
        get { lock (_gate) return _completionTokens; }
        set { lock (_gate) _completionTokens = value; }
    }

    /// <summary>
    /// Strongly-typed value for <see cref="HookMetadataKeys.LmCallDurationMs"/>. Net-new in v1.0-rc1 alpha.6.
    /// </summary>
    public double? LmCallDurationMs
    {
        get { lock (_gate) return _lmCallDurationMs; }
        set { lock (_gate) _lmCallDurationMs = value; }
    }

    /// <summary>Writes a value into the metadata bag. Visible to subsequent hooks in this call.</summary>
    public void Set(string key, object? value)
    {
        // Fast-path the four LM-result keys to typed slots (no boxing of long/double).
        switch (key)
        {
            case HookMetadataKeys.ModelId:
                lock (_gate) _modelId = value as string;
                return;
            case HookMetadataKeys.PromptTokens:
                lock (_gate) _promptTokens = value is long pl ? pl : (value is null ? null : Convert.ToInt64(value));
                return;
            case HookMetadataKeys.CompletionTokens:
                lock (_gate) _completionTokens = value is long cl ? cl : (value is null ? null : Convert.ToInt64(value));
                return;
            case HookMetadataKeys.LmCallDurationMs:
                lock (_gate) _lmCallDurationMs = value is double d ? d : (value is null ? null : Convert.ToDouble(value));
                return;
        }
        lock (_gate)
        {
            _extras ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            _extras[key] = value;
        }
    }

    // ------ IReadOnlyDictionary<string, object?> projection (typed slots + lazy extras) ------

    /// <inheritdoc />
    public bool ContainsKey(string key)
    {
        switch (key)
        {
            case HookMetadataKeys.ModelId:           lock (_gate) return _modelId is not null;
            case HookMetadataKeys.PromptTokens:      lock (_gate) return _promptTokens.HasValue;
            case HookMetadataKeys.CompletionTokens:  lock (_gate) return _completionTokens.HasValue;
            case HookMetadataKeys.LmCallDurationMs:  lock (_gate) return _lmCallDurationMs.HasValue;
        }
        lock (_gate) return _extras is not null && _extras.ContainsKey(key);
    }

    /// <inheritdoc />
    public bool TryGetValue(string key, out object? value)
    {
        switch (key)
        {
            case HookMetadataKeys.ModelId:
                lock (_gate)
                {
                    value = _modelId;
                    return _modelId is not null;
                }
            case HookMetadataKeys.PromptTokens:
                lock (_gate)
                {
                    if (_promptTokens.HasValue) { value = _promptTokens.Value; return true; }
                    value = null; return false;
                }
            case HookMetadataKeys.CompletionTokens:
                lock (_gate)
                {
                    if (_completionTokens.HasValue) { value = _completionTokens.Value; return true; }
                    value = null; return false;
                }
            case HookMetadataKeys.LmCallDurationMs:
                lock (_gate)
                {
                    if (_lmCallDurationMs.HasValue) { value = _lmCallDurationMs.Value; return true; }
                    value = null; return false;
                }
        }
        lock (_gate)
        {
            if (_extras is not null && _extras.TryGetValue(key, out value)) return true;
            value = null;
            return false;
        }
    }

    /// <inheritdoc />
    public object? this[string key]
    {
        get
        {
            if (TryGetValue(key, out var v)) return v;
            throw new KeyNotFoundException(key);
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> Keys
    {
        get
        {
            foreach (var kv in EnumerateAll()) yield return kv.Key;
        }
    }

    /// <inheritdoc />
    public IEnumerable<object?> Values
    {
        get
        {
            foreach (var kv in EnumerateAll()) yield return kv.Value;
        }
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                if (_modelId is not null) n++;
                if (_promptTokens.HasValue) n++;
                if (_completionTokens.HasValue) n++;
                if (_lmCallDurationMs.HasValue) n++;
                if (_extras is not null) n += _extras.Count;
                return n;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => EnumerateAll().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    IEnumerable<KeyValuePair<string, object?>> EnumerateAll()
    {
        // Snapshot under lock to avoid concurrent-modification on the rare parallel-write contract.
        string? modelId;
        long? promptTokens;
        long? completionTokens;
        double? lmCallDurationMs;
        KeyValuePair<string, object?>[]? extrasSnapshot = null;
        lock (_gate)
        {
            modelId = _modelId;
            promptTokens = _promptTokens;
            completionTokens = _completionTokens;
            lmCallDurationMs = _lmCallDurationMs;
            if (_extras is { Count: > 0 })
            {
                extrasSnapshot = new KeyValuePair<string, object?>[_extras.Count];
                var i = 0;
                foreach (var kv in _extras) extrasSnapshot[i++] = kv;
            }
        }
        if (modelId is not null)
            yield return new KeyValuePair<string, object?>(HookMetadataKeys.ModelId, modelId);
        if (promptTokens.HasValue)
            yield return new KeyValuePair<string, object?>(HookMetadataKeys.PromptTokens, promptTokens.Value);
        if (completionTokens.HasValue)
            yield return new KeyValuePair<string, object?>(HookMetadataKeys.CompletionTokens, completionTokens.Value);
        if (lmCallDurationMs.HasValue)
            yield return new KeyValuePair<string, object?>(HookMetadataKeys.LmCallDurationMs, lmCallDurationMs.Value);
        if (extrasSnapshot is not null)
        {
            for (var i = 0; i < extrasSnapshot.Length; i++) yield return extrasSnapshot[i];
        }
    }
}

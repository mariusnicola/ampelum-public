using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SignalGrid.Ampelum.Composition;

/// <summary>Stops when |current - previous| &lt; epsilon for <c>state.CurrentValue.&lt;field&gt;</c>
/// compared against the prior iteration. Returns <see langword="false"/> on iteration 1.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class FieldChangeBelow<T> : LoopPredicate<T> where T : ISignature
{
    readonly string _field;
    readonly decimal _epsilon;

    /// <summary>Initialises a <see cref="FieldChangeBelow{T}"/> predicate.</summary>
    public FieldChangeBelow(string field, decimal epsilon)
    {
        ArgumentNullException.ThrowIfNull(field);
        _field = field;
        _epsilon = epsilon;
    }

    /// <inheritdoc/>
    public override bool ShouldStop(LoopState<T> state)
    {
        // Iteration 1 has no prior value to compare against.
        if (state.Iteration <= 1 || state.History.Count < 1) return false;
        var prop = typeof(T).GetProperty(_field, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{_field}' not found on {typeof(T).Name}.");
        var current = Convert.ToDecimal(prop.GetValue(state.CurrentValue)
            ?? throw new InvalidOperationException($"Field '{_field}' on current value is null."));
        // Compares History[^1].Value vs CurrentValue (previous prediction's value).
        var previous = state.History[^1].Value;
        var prev = Convert.ToDecimal(prop.GetValue(previous)
            ?? throw new InvalidOperationException($"Field '{_field}' on previous value is null."));
        var delta = current - prev;
        if (delta < 0m) delta = -delta;
        return delta < _epsilon;
    }

    /// <inheritdoc/>
    public override IReadOnlyDictionary<string, object?> Parameters => new Dictionary<string, object?>
    {
        ["field"] = _field,
        ["epsilon"] = _epsilon,
    };

    /// <inheritdoc/>
    public override LoopPredicate<T> WithParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var field = parameters.TryGetValue("field", out var f) && f is string fs ? fs : _field;
        var eps = parameters.TryGetValue("epsilon", out var e) && e is decimal ed ? ed : _epsilon;
        return new FieldChangeBelow<T>(field, eps);
    }
}

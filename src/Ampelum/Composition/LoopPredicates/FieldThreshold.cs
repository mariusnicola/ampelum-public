using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SignalGrid.Ampelum.Composition;

/// <summary>Stops when <c>state.CurrentValue.&lt;field&gt;</c> compares against <c>threshold</c> per <c>direction</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class FieldThreshold<T> : LoopPredicate<T> where T : ISignature
{
    readonly string _field;
    readonly decimal _threshold;
    readonly ThresholdDirection _direction;

    /// <summary>Initialises a <see cref="FieldThreshold{T}"/> predicate.</summary>
    public FieldThreshold(string field, decimal threshold, ThresholdDirection direction)
    {
        ArgumentNullException.ThrowIfNull(field);
        _field = field;
        _threshold = threshold;
        _direction = direction;
    }

    /// <inheritdoc/>
    public override bool ShouldStop(LoopState<T> state)
    {
        var prop = typeof(T).GetProperty(_field, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{_field}' not found on {typeof(T).Name}.");
        var raw = prop.GetValue(state.CurrentValue)
            ?? throw new InvalidOperationException($"Field '{_field}' on {typeof(T).Name} is null.");
        var v = Convert.ToDecimal(raw);
        return _direction switch
        {
            ThresholdDirection.LessThan => v < _threshold,
            ThresholdDirection.LessThanOrEqual => v <= _threshold,
            ThresholdDirection.GreaterThan => v > _threshold,
            _ => v >= _threshold,
        };
    }

    /// <inheritdoc/>
    public override IReadOnlyDictionary<string, object?> Parameters => new Dictionary<string, object?>
    {
        ["field"] = _field,
        ["threshold"] = _threshold,
        ["direction"] = _direction,
    };

    /// <inheritdoc/>
    public override LoopPredicate<T> WithParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var field = parameters.TryGetValue("field", out var f) && f is string fs ? fs : _field;
        var threshold = parameters.TryGetValue("threshold", out var t) && t is decimal td ? td : _threshold;
        var direction = parameters.TryGetValue("direction", out var d) && d is ThresholdDirection dd ? dd : _direction;
        return new FieldThreshold<T>(field, threshold, direction);
    }
}

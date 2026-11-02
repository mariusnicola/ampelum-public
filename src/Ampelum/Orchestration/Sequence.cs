using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>Sequential composition of 2 stages.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Sequence<T1, T2> : IModule<T1, T2>
{
    readonly IModule<T1, T2> _stage1;

    /// <summary>Creates a 2-stage sequence with the given stage.</summary>
    public Sequence(IModule<T1, T2> stage1)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        _stage1 = stage1;
    }

    /// <inheritdoc/>
    public async Task<Prediction<T2>> ForwardAsync(T1 inputs, CancellationToken ct = default)
    {
        var r1 = await _stage1.ForwardAsync(inputs, ct);
        return r1;
    }
}

/// <summary>Sequential composition of 3 stages.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Sequence<T1, T2, T3> : IModule<T1, T3>
{
    readonly IModule<T1, T2> _stage1;
    readonly IModule<T2, T3> _stage2;

    /// <summary>Creates a 3-stage sequence with the given stages.</summary>
    public Sequence(IModule<T1, T2> stage1, IModule<T2, T3> stage2)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        ArgumentNullException.ThrowIfNull(stage2);
        _stage1 = stage1;
        _stage2 = stage2;
    }

    /// <inheritdoc/>
    public async Task<Prediction<T3>> ForwardAsync(T1 inputs, CancellationToken ct = default)
    {
        var r1 = await _stage1.ForwardAsync(inputs, ct);
        var r2 = await _stage2.ForwardAsync(r1.Value, ct);

        var childTraces = new List<TraceRecord> { r1.Trace, r2.Trace };
        var childIds = childTraces.Select(t => t.Id).ToArray();
        var extraMeta = new Dictionary<string, object?>
        {
            [Composition.TraceKeys.CompositionPrimitive] = "Sequence",
            [Composition.TraceKeys.SequenceLength] = 2,
            [Composition.TraceKeys.SequenceChildTraceIds] = (IReadOnlyList<Guid>)childIds,
        };
        var trace = Composition.CompositionTrace.Aggregate(
            id: Guid.NewGuid(),
            moduleName: $"Sequence<{typeof(T1).Name},{typeof(T2).Name},{typeof(T3).Name}>",
            signatureType: typeof(T3),
            inputs: new Dictionary<string, object?> { ["stages"] = 2 },
            outputs: new Dictionary<string, object?> { ["result"] = r2.Value! },
            childTraces: childTraces,
            extraMetadata: extraMeta);

        return new Prediction<T3>(r2.Value, trace);
    }
}

/// <summary>Sequential composition of 4 stages.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Sequence<T1, T2, T3, T4> : IModule<T1, T4>
{
    readonly IModule<T1, T2> _stage1;
    readonly IModule<T2, T3> _stage2;
    readonly IModule<T3, T4> _stage3;

    /// <summary>Creates a 4-stage sequence with the given stages.</summary>
    public Sequence(
        IModule<T1, T2> stage1, IModule<T2, T3> stage2, IModule<T3, T4> stage3)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        ArgumentNullException.ThrowIfNull(stage2);
        ArgumentNullException.ThrowIfNull(stage3);
        _stage1 = stage1;
        _stage2 = stage2;
        _stage3 = stage3;
    }

    /// <inheritdoc/>
    public async Task<Prediction<T4>> ForwardAsync(T1 inputs, CancellationToken ct = default)
    {
        var r1 = await _stage1.ForwardAsync(inputs, ct);
        var r2 = await _stage2.ForwardAsync(r1.Value, ct);
        var r3 = await _stage3.ForwardAsync(r2.Value, ct);
        var childTraces = new List<TraceRecord> { r1.Trace, r2.Trace, r3.Trace };
        var extraMeta = new Dictionary<string, object?>
        {
            [Composition.TraceKeys.CompositionPrimitive] = "Sequence",
            [Composition.TraceKeys.SequenceLength] = 3,
            [Composition.TraceKeys.SequenceChildTraceIds] = (IReadOnlyList<Guid>)childTraces.Select(t => t.Id).ToArray(),
        };
        var trace = Composition.CompositionTrace.Aggregate(
            id: Guid.NewGuid(),
            moduleName: $"Sequence<{typeof(T1).Name},{typeof(T2).Name},{typeof(T3).Name},{typeof(T4).Name}>",
            signatureType: typeof(T4),
            inputs: new Dictionary<string, object?> { ["stages"] = 3 },
            outputs: new Dictionary<string, object?> { ["result"] = r3.Value! },
            childTraces: childTraces,
            extraMetadata: extraMeta);
        return new Prediction<T4>(r3.Value, trace);
    }
}

/// <summary>Sequential composition of 5 stages.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Sequence<T1, T2, T3, T4, T5> : IModule<T1, T5>
{
    readonly IModule<T1, T2> _stage1;
    readonly IModule<T2, T3> _stage2;
    readonly IModule<T3, T4> _stage3;
    readonly IModule<T4, T5> _stage4;

    /// <summary>Creates a 5-stage sequence with the given stages.</summary>
    public Sequence(
        IModule<T1, T2> stage1, IModule<T2, T3> stage2,
        IModule<T3, T4> stage3, IModule<T4, T5> stage4)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        ArgumentNullException.ThrowIfNull(stage2);
        ArgumentNullException.ThrowIfNull(stage3);
        ArgumentNullException.ThrowIfNull(stage4);
        _stage1 = stage1;
        _stage2 = stage2;
        _stage3 = stage3;
        _stage4 = stage4;
    }

    /// <inheritdoc/>
    public async Task<Prediction<T5>> ForwardAsync(T1 inputs, CancellationToken ct = default)
    {
        var r1 = await _stage1.ForwardAsync(inputs, ct);
        var r2 = await _stage2.ForwardAsync(r1.Value, ct);
        var r3 = await _stage3.ForwardAsync(r2.Value, ct);
        var r4 = await _stage4.ForwardAsync(r3.Value, ct);
        var childTraces = new List<TraceRecord> { r1.Trace, r2.Trace, r3.Trace, r4.Trace };
        var extraMeta = new Dictionary<string, object?>
        {
            [Composition.TraceKeys.CompositionPrimitive] = "Sequence",
            [Composition.TraceKeys.SequenceLength] = 4,
            [Composition.TraceKeys.SequenceChildTraceIds] = (IReadOnlyList<Guid>)childTraces.Select(t => t.Id).ToArray(),
        };
        var trace = Composition.CompositionTrace.Aggregate(
            id: Guid.NewGuid(),
            moduleName: $"Sequence<{typeof(T1).Name},{typeof(T2).Name},{typeof(T3).Name},{typeof(T4).Name},{typeof(T5).Name}>",
            signatureType: typeof(T5),
            inputs: new Dictionary<string, object?> { ["stages"] = 4 },
            outputs: new Dictionary<string, object?> { ["result"] = r4.Value! },
            childTraces: childTraces,
            extraMetadata: extraMeta);
        return new Prediction<T5>(r4.Value, trace);
    }
}
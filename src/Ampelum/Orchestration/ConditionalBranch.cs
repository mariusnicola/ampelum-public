using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>
/// Type-safe conditional branching primitive. Evaluates a condition module and dispatches
/// to either the <c>then</c> or <c>else</c> branch based on the boolean result.
/// </summary>
/// <typeparam name="TC">Input signature type shared by the condition and both branches.</typeparam>
/// <typeparam name="TT">Output signature type of the if-true branch.</typeparam>
/// <typeparam name="TF">Output signature type of the if-false branch.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ConditionalBranch<TC, TT, TF> : IModule<TC, object>
    where TC : ISignature
    where TT : ISignature
    where TF : ISignature
{
    readonly IModule<TC, bool> _condition;
    readonly IModule<TC, TT> _thenBranch;
    readonly IModule<TC, TF> _elseBranch;

    /// <summary>Creates a conditional branch.</summary>
    public ConditionalBranch(
        IModule<TC, bool> condition,
        IModule<TC, TT> thenBranch,
        IModule<TC, TF> elseBranch)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(thenBranch);
        ArgumentNullException.ThrowIfNull(elseBranch);
        _condition = condition;
        _thenBranch = thenBranch;
        _elseBranch = elseBranch;
    }

    /// <inheritdoc/>
    public async Task<Prediction<object>> ForwardAsync(TC inputs, CancellationToken ct = default)
    {
        // Evaluate condition
        var conditionResult = await _condition.ForwardAsync(inputs, ct);
        var branchTaken = conditionResult.Value;
        var branchResult = branchTaken
            ? (object)await _thenBranch.ForwardAsync(inputs, ct)
            : await _elseBranch.ForwardAsync(inputs, ct);

        // Resolve traces
        var thenPred = branchResult as Prediction<TT>;
        var elsePred = branchResult as Prediction<TF>;
        var chosenTrace = branchTaken ? thenPred!.Trace : elsePred!.Trace;
        var chosenValue = branchTaken ? (object)thenPred!.Value! : (object)elsePred!.Value!;

        var childTraces = new List<TraceRecord> { conditionResult.Trace, chosenTrace };
        var childIds = childTraces.Select(t => t.Id).ToArray();
        var extraMeta = new Dictionary<string, object?>
        {
            [Composition.TraceKeys.CompositionPrimitive] = "ConditionalBranch",
            [Composition.TraceKeys.BranchRoute] = branchTaken ? "ifTrue" : "ifFalse",
            [Composition.TraceKeys.BranchChildTraceIds] = (IReadOnlyList<Guid>)childIds,
        };
        var trace = Composition.CompositionTrace.Aggregate(
            id: Guid.NewGuid(),
            moduleName: $"ConditionalBranch<{typeof(TC).Name},{typeof(TT).Name},{typeof(TF).Name}>",
            signatureType: branchTaken ? typeof(TT) : typeof(TF),
            inputs: new Dictionary<string, object?> { ["branch"] = branchTaken ? "then" : "else" },
            outputs: new Dictionary<string, object?> { ["value"] = chosenValue },
            childTraces: childTraces,
            extraMetadata: extraMeta);

        return new Prediction<object>(chosenValue, trace);
    }
}
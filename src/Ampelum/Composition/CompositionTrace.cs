using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Composition;

/// <summary>
/// Builder for an aggregate <see cref="TraceRecord"/> that folds N child traces (one per
/// inner LM call) into a single parent record written by a composition primitive
/// (Loop / Sequence / ConditionalBranch). User-defined primitives may call this directly.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class CompositionTrace
{
    /// <summary>
    /// Build the aggregate trace. See spec Â§7 for field-by-field rules:
    /// <list type="bullet">
    ///   <item><description><c>PromptMessages</c>: concatenation of every child's PromptMessages, in order.</description></item>
    ///   <item><description><c>RawResponse</c>: <c>" | "</c>-joined non-empty child raw responses.</description></item>
    ///   <item><description><c>Usage</c>: per-field sum of non-null child <see cref="UsageDetails"/>.</description></item>
    ///   <item><description><c>Duration</c>: sum of child <c>Duration</c>.</description></item>
    ///   <item><description><c>Error</c>: first non-null child <c>Error</c>; <see langword="null"/> when all clean.</description></item>
    ///   <item><description><c>Metadata</c>: <paramref name="extraMetadata"/> merged over the well-known
    ///     <see cref="TraceKeys.CompositionChildCount"/> and <see cref="TraceKeys.CompositionChildTraceIds"/> keys.
    ///     <paramref name="extraMetadata"/> wins on key collision.</description></item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="childTraces"/> is empty.</exception>
    public static TraceRecord Aggregate(
        Guid id,
        string moduleName,
        Type signatureType,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, object?> outputs,
        IReadOnlyList<TraceRecord> childTraces,
        IReadOnlyDictionary<string, object?>? extraMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(moduleName);
        ArgumentNullException.ThrowIfNull(signatureType);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(childTraces);
        if (childTraces.Count == 0)
            throw new ArgumentException("At least one child trace is required.", nameof(childTraces));

        // ---- PromptMessages: in-order concatenation ----
        var promptCount = 0;
        for (var i = 0; i < childTraces.Count; i++) promptCount += childTraces[i].PromptMessages.Count;
        var prompt = new List<ChatMessage>(promptCount);
        for (var i = 0; i < childTraces.Count; i++)
        {
            var pm = childTraces[i].PromptMessages;
            for (var j = 0; j < pm.Count; j++) prompt.Add(pm[j]);
        }

        // ---- RawResponse: " | " join over non-empty entries ----
        List<string>? raws = null;
        for (var i = 0; i < childTraces.Count; i++)
        {
            var r = childTraces[i].RawResponse;
            if (!string.IsNullOrEmpty(r))
            {
                raws ??= new List<string>(childTraces.Count);
                raws.Add(r);
            }
        }
        var rawJoined = raws is null ? string.Empty : string.Join(" | ", raws);

        // ---- Usage: sum of non-null child counters ----
        long? input = null, output = null, total = null;
        var anyUsage = false;
        for (var i = 0; i < childTraces.Count; i++)
        {
            var u = childTraces[i].Usage;
            if (u is null) continue;
            anyUsage = true;
            if (u.InputTokenCount is long it) input = (input ?? 0L) + it;
            if (u.OutputTokenCount is long ot) output = (output ?? 0L) + ot;
            if (u.TotalTokenCount is long tt) total = (total ?? 0L) + tt;
        }
        UsageDetails? aggUsage = anyUsage
            ? new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = total }
            : null;

        // ---- Duration: sum ----
        var dur = TimeSpan.Zero;
        for (var i = 0; i < childTraces.Count; i++) dur += childTraces[i].Duration;

        // ---- Error: first non-null wins ----
        Exception? firstError = null;
        for (var i = 0; i < childTraces.Count; i++)
        {
            if (childTraces[i].Error is { } e) { firstError = e; break; }
        }

        // ---- Metadata: defaults THEN extras (extras win on collision) ----
        var ids = new Guid[childTraces.Count];
        for (var i = 0; i < childTraces.Count; i++) ids[i] = childTraces[i].Id;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [TraceKeys.CompositionChildCount] = childTraces.Count,
            [TraceKeys.CompositionChildTraceIds] = (IReadOnlyList<Guid>)ids,
        };
        if (extraMetadata is not null)
        {
            foreach (var kv in extraMetadata) metadata[kv.Key] = kv.Value;
        }

        return new TraceRecord(
            Id: id,
            ModuleName: moduleName,
            SignatureType: signatureType,
            Inputs: inputs,
            Outputs: outputs,
            PromptMessages: prompt,
            RawResponse: rawJoined,
            Usage: aggUsage,
            Duration: dur,
            Error: firstError,
            Metadata: metadata);
    }
}

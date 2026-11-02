using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>
/// Pure mapper from <see cref="TraceRecord"/> to <see cref="LangSmithRun"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plan deviation (v0.9e):</b> The plan PSQ-2 specifies mapping
/// <c>gen_ai.system</c> and <c>gen_ai.request.model</c> off the OTel
/// <see cref="System.Diagnostics.Activity"/> tag bag. Today
/// <see cref="TraceRecord"/> does not capture those tags, so this mapper omits
/// the provider/model attribution. A follow-up will thread these into
/// <see cref="TraceRecord.Metadata"/> so the exporters can emit them without
/// needing access to <c>Activity.Current</c> from the drain task.
/// </para>
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangSmithRunMapper
{
    static readonly FrozenDictionary<string, string> ModuleToRunType = new Dictionary<string, string>
    {
        ["Predict"] = "llm",
        ["ChainOfThought"] = "chain",
        ["ReAct"] = "tool",
        ["Retriever"] = "retriever",
        ["EmbeddingGenerator"] = "embedding",
    }.ToFrozenDictionary();

    readonly TimeProvider _time;

    /// <summary>Creates a mapper using <see cref="TimeProvider.System"/>.</summary>
    public LangSmithRunMapper() : this(TimeProvider.System) { }

    /// <summary>Creates a mapper backed by <paramref name="timeProvider"/>; intended for tests.</summary>
    public LangSmithRunMapper(TimeProvider timeProvider) => _time = timeProvider;

    /// <summary>Translates a single <see cref="TraceRecord"/> to a <see cref="LangSmithRun"/>.</summary>
    public LangSmithRun Map(TraceRecord record, string projectName, bool includeAuditPayload)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(projectName);

        var endTime = _time.GetUtcNow();
        var startTime = endTime - record.Duration;
        var runType = ModuleToRunType.TryGetValue(record.ModuleName, out var rt) ? rt : "chain";

        var runtime = new Dictionary<string, object?>
        {
            ["library"] = "ampelum",
            ["langsmith_schema_version"] = LangSmithSchemaVersion.SemanticConventionsVersion,
        };
        if (record.Usage is { } u)
        {
            runtime["token_usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = u.InputTokenCount,
                ["output_tokens"] = u.OutputTokenCount,
                ["total_tokens"] = u.TotalTokenCount,
            };
        }

        var metadata = new Dictionary<string, object?>
        {
            ["ampelum.module"] = record.ModuleName,
            ["ampelum.signature_type"] = record.SignatureType.Name,
            ["ampelum.trace_id"] = record.Id.ToString("D"),
        };
        foreach (var kv in record.Metadata)
        {
            metadata[$"ampelum.{kv.Key}"] = kv.Value;
        }

        var extra = new Dictionary<string, object?>
        {
            ["runtime"] = runtime,
            ["metadata"] = metadata,
        };

        return new LangSmithRun
        {
            Id = record.Id,
            Name = record.ModuleName,
            RunType = runType,
            StartTime = startTime,
            EndTime = endTime,
            Inputs = includeAuditPayload ? record.Inputs : null,
            Outputs = includeAuditPayload && record.Error is null ? record.Outputs : null,
            Extra = extra,
            Error = record.Error?.Message,
            SessionName = projectName,
        };
    }
}

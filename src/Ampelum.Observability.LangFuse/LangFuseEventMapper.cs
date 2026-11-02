using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>
/// Pure mapper from <see cref="TraceRecord"/> to a pair of LangFuse ingestion
/// events: one <c>trace-create</c> (the parent) and one <c>generation-create</c>
/// (the LLM call). Mirrors the most common TraceRecord shape (a single
/// ForwardAsync-spanned LM invocation).
/// </summary>
/// <remarks>
/// Plan deviation (v0.9e): the master plan describes a richer trace/span/
/// generation hierarchy derived from <c>Activity.ParentSpanId</c>. Today
/// <see cref="TraceRecord"/> is flat (one record per ForwardAsync call), so
/// this mapper emits a 2-event sequence — adequate for LangFuse to render the
/// per-module timeline with token usage. A follow-up will extend the mapper
/// when nested-span trace records land.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangFuseEventMapper
{
    readonly TimeProvider _time;

    /// <summary>Creates a mapper using <see cref="TimeProvider.System"/>.</summary>
    public LangFuseEventMapper() : this(TimeProvider.System) { }

    /// <summary>Creates a mapper backed by <paramref name="timeProvider"/>; intended for tests.</summary>
    public LangFuseEventMapper(TimeProvider timeProvider) => _time = timeProvider;

    /// <summary>Maps a single <see cref="TraceRecord"/> to a LangFuse ingestion event sequence.</summary>
    public IReadOnlyList<LangFuseEvent> Map(TraceRecord record, bool includeAuditPayload)
    {
        ArgumentNullException.ThrowIfNull(record);
        var traceId = record.Id.ToString("N");
        var generationId = $"{traceId}-gen";
        var endTime = _time.GetUtcNow();
        var startTime = endTime - record.Duration;

        var metadata = new Dictionary<string, object?>
        {
            ["ampelum.module"] = record.ModuleName,
            ["ampelum.signature_type"] = record.SignatureType.Name,
        };
        foreach (var kv in record.Metadata)
        {
            metadata[$"ampelum.{kv.Key}"] = kv.Value;
        }

        var traceEvent = new LangFuseEvent
        {
            Id = Guid.NewGuid(),
            Type = "trace-create",
            Timestamp = startTime,
            Body = new LangFuseTraceBody
            {
                Id = traceId,
                Name = record.ModuleName,
                Metadata = metadata,
                Tags = new[] { "ampelum" },
            },
        };

        var usage = record.Usage is { } u
            ? new LangFuseUsage { Input = u.InputTokenCount, Output = u.OutputTokenCount, Total = u.TotalTokenCount }
            : null;

        var generationEvent = new LangFuseEvent
        {
            Id = Guid.NewGuid(),
            Type = "generation-create",
            Timestamp = endTime,
            Body = new LangFuseGenerationBody
            {
                Id = generationId,
                TraceId = traceId,
                Name = record.ModuleName,
                StartTime = startTime,
                EndTime = endTime,
                Usage = usage,
                Metadata = metadata,
                Input = includeAuditPayload ? record.Inputs : null,
                Output = includeAuditPayload && record.Error is null ? record.Outputs : null,
                Level = record.Error is null ? "DEFAULT" : "ERROR",
                StatusMessage = record.Error?.Message,
            },
        };

        return new[] { traceEvent, generationEvent };
    }
}

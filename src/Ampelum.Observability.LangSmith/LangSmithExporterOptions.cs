using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>Tunable options for <see cref="LangSmithExporter"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangSmithExporterOptions
{
    /// <summary>Maximum runs flushed in a single <c>/runs</c> POST. LangSmith hard-limit is 100.</summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>Bounded queue capacity. Overflow drops the new record per best-effort trace semantics.</summary>
    public int QueueCapacity { get; set; } = 2048;

    /// <summary>
    /// When <see langword="true"/>, populates <c>inputs</c>/<c>outputs</c> from
    /// <see cref="TraceRecord.Inputs"/> and <see cref="TraceRecord.Outputs"/>. Default
    /// <see langword="false"/> per the v1.0 spec G3 PII boundary.
    /// </summary>
    public bool IncludeAuditPayload { get; set; }

    /// <summary>Per-flush HTTP timeout. Overruns increment the failure counter and drop the batch.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

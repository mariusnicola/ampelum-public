using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>Tunable options for <see cref="LangFuseExporter"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangFuseExporterOptions
{
    /// <summary>Maximum events flushed in a single <c>/ingestion</c> POST.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Bounded queue capacity. Overflow drops the new record.</summary>
    public int QueueCapacity { get; set; } = 2048;

    /// <summary>When <see langword="true"/>, populates the event body's <c>input</c>/<c>output</c> from <see cref="TraceRecord"/>.</summary>
    public bool IncludeAuditPayload { get; set; }

    /// <summary>Per-flush HTTP timeout.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

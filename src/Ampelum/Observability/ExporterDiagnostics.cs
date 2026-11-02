using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace SignalGrid.Ampelum.Observability;

/// <summary>
/// Counters and helpers shared by out-of-tree observability exporters
/// (LangSmith, LangFuse, Phoenix). Sits under <see cref="Diagnostics.Meter"/>
/// so a single OTel SDK registration captures every exporter's health
/// signal alongside core Ampelum metrics.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class ExporterDiagnostics
{
    /// <summary>Trace records dropped by an exporter due to bounded-queue overflow.</summary>
    public static readonly Counter<long> ExporterDropped =
        Diagnostics.Meter.CreateCounter<long>(
            "SignalGrid.Ampelum.exporter.dropped",
            "{records}",
            "Trace records dropped by an exporter due to queue overflow.");

    /// <summary>Number of times an exporter's HTTP/transport call failed (post-retry).</summary>
    public static readonly Counter<long> ExporterFailures =
        Diagnostics.Meter.CreateCounter<long>(
            "SignalGrid.Ampelum.exporter.failures",
            "{failures}",
            "Number of times an exporter's transport call failed after retries.");

    /// <summary>Number of times an exporter received an authentication failure (401/403) from the remote.</summary>
    public static readonly Counter<long> ExporterAuthFailures =
        Diagnostics.Meter.CreateCounter<long>(
            "SignalGrid.Ampelum.exporter.auth_failures",
            "{failures}",
            "Number of times an exporter received a 401/403 from the remote.");

    /// <summary>Records a drop event tagged with the exporter name.</summary>
    public static void RecordExporterDrop(string exporter) =>
        ExporterDropped.Add(1, new KeyValuePair<string, object?>("exporter", exporter));

    /// <summary>Records a transport failure tagged with the exporter name and exception type.</summary>
    public static void RecordExporterFailure(string exporter, Exception exception) =>
        ExporterFailures.Add(
            1,
            new KeyValuePair<string, object?>("exporter", exporter),
            new KeyValuePair<string, object?>("exception.type", exception.GetType().FullName ?? "unknown"));

    /// <summary>Records an authentication failure tagged with the exporter name.</summary>
    public static void RecordExporterAuthFailure(string exporter) =>
        ExporterAuthFailures.Add(1, new KeyValuePair<string, object?>("exporter", exporter));
}

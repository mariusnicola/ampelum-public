using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace SignalGrid.Ampelum.Host.Tracing;

/// <summary>Shared OpenTelemetry metrics instruments for all Ampelum Host endpoints.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class HostMetrics
{
    /// <summary>The meter name used when registering with OpenTelemetry.</summary>
    public const string Name = "SignalGrid.Ampelum.Host";
    /// <summary>The shared <see cref="System.Diagnostics.Metrics.Meter"/> instance.</summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>Number of requests received, tagged by module and status.</summary>
    public static readonly Counter<long> RequestCount =
        Meter.CreateCounter<long>("ampelum.host.request.count");

    /// <summary>End-to-end request duration in milliseconds.</summary>
    public static readonly Histogram<double> RequestDurationMs =
        Meter.CreateHistogram<double>("ampelum.host.request.duration", unit: "ms");

    /// <summary>Number of SSE events emitted across all streaming requests.</summary>
    public static readonly Counter<long> SseEventsEmitted =
        Meter.CreateCounter<long>("ampelum.host.sse.events.emitted");
}

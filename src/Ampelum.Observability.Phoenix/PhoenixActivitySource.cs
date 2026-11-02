using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.Phoenix;

/// <summary>
/// Private <see cref="ActivitySource"/> the Phoenix exporter uses to mirror
/// each <see cref="TraceRecord"/>. The host OpenTelemetry SDK must register
/// this source name (<c>Ampelum.Observability.Phoenix</c>) on the
/// <c>TracerProvider</c> for spans to flow to the OTLP collector.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class PhoenixActivitySource
{
    /// <summary>Source name expected on the host's <c>TracerProvider.AddSource(...)</c> registration.</summary>
    public const string Name = "SignalGrid.Ampelum.Observability.Phoenix";

    /// <summary>The shared <see cref="ActivitySource"/> instance.</summary>
    public static readonly ActivitySource Source = new(Name);
}

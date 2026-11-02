using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.Phoenix;

/// <summary>Optional Phoenix collector configuration. Used by host OTLP exporter.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PhoenixCredentials
{
    /// <summary>Phoenix collector endpoint. Defaults to local <c>http://localhost:6006</c>.</summary>
    public Uri Endpoint { get; set; } = new("http://localhost:6006");

    /// <summary>Optional API key for Phoenix Cloud (sent as <c>api_key</c> header by the OTLP exporter).</summary>
    public string? ApiKey { get; set; }
}

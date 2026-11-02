using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Observability.Phoenix;

/// <summary>
/// <see cref="ITraceSink"/> decorator that mirrors each <see cref="TraceRecord"/>
/// onto a private <see cref="ActivitySource"/> (<see cref="PhoenixActivitySource.Name"/>)
/// with Phoenix-friendly attribute aliases. The host's OpenTelemetry SDK is
/// responsible for forwarding the resulting spans to the Phoenix collector via
/// OTLP — call <c>tracerProviderBuilder.AddSource(PhoenixActivitySource.Name)</c>
/// + <c>.AddOtlpExporter(...)</c> in your <c>Program.cs</c>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PhoenixExporter : ITraceSink
{
    readonly ITraceSink _inner;
    readonly PhoenixSpanMapper _mapper;
    readonly PhoenixExporterOptions _options;

    /// <summary>Creates a new exporter wrapping <paramref name="inner"/>.</summary>
    public PhoenixExporter(ITraceSink inner, PhoenixSpanMapper mapper, IOptions<PhoenixExporterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _mapper = mapper;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public void Write(TraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try { _inner.Write(record); }
        catch { /* must-not-throw */ }

        // Phoenix mirror: synthesize a span on the private ActivitySource with the
        // record's wall-clock duration. ActivityListener-aware OTel SDKs will
        // capture and forward via the configured OTLP exporter.
        var endTime = DateTime.UtcNow;
        var startTime = endTime - record.Duration;
        using var activity = PhoenixActivitySource.Source.StartActivity(
            record.ModuleName,
            ActivityKind.Internal,
            parentContext: default,
            tags: null,
            links: null,
            startTime: startTime);
        if (activity is null) return; // No listener — silently drop.
        _mapper.Apply(activity, record, _options);
        activity.SetEndTime(endTime);
    }
}

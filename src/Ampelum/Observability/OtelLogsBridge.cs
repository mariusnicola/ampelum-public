using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum.Observability;

/// <summary>
/// Helper that lifts <c>gen_ai.*</c> OTel GenAI-semconv attributes from the
/// active <see cref="Activity"/> tag bag into a structured <see cref="ILogger"/>
/// scope. Inside that scope, every <c>AmpelumLog.*</c> call (and any other
/// <see cref="ILogger"/> emission) carries the attributes as named scope state
/// â€” which the OTel logging provider promotes onto the resulting
/// <c>LogRecord</c> so downstream backends (Application Insights, OTLP,
/// LangSmith, LangFuse, Phoenix) can filter / pivot on them.
/// </summary>
/// <remarks>
/// <para>
/// This bridge does <b>not</b> take a hard dependency on
/// <c>OpenTelemetry</c> or <c>OpenTelemetry.Extensions.Hosting</c>. The host
/// retains full control of the OTel pipeline:
/// </para>
/// <code>
/// services.AddLogging(b =&gt; b.AddOpenTelemetry(o =&gt;
/// {
///     o.IncludeScopes = true;          // REQUIRED for the bridge
///     o.ParseStateValues = true;       // REQUIRED for the bridge
///     o.AddOtlpExporter(...);
/// }));
/// </code>
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class OtelLogsBridge
{
    /// <summary>
    /// Names of <c>gen_ai.*</c> tags this bridge will copy from
    /// <see cref="Activity.Current"/> into the logger scope. Bounded by
    /// the v0.7 high-cardinality allowlist.
    /// </summary>
    public static readonly IReadOnlyList<string> GenAiTagNames = new[]
    {
        "gen_ai.system",
        "gen_ai.request.model",
        "gen_ai.response.model",
        "gen_ai.response.id",
        "gen_ai.usage.input_tokens",
        "gen_ai.usage.output_tokens",
        "gen_ai.operation.name",
    };

    /// <summary>
    /// Begins an <see cref="ILogger"/> scope populated with every
    /// <c>gen_ai.*</c> tag present on <see cref="Activity.Current"/>.
    /// Returns <see langword="null"/> when no ambient activity exists, in
    /// which case no scope is opened.
    /// </summary>
    public static IDisposable? BeginGenAiScope(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var activity = Activity.Current;
        if (activity is null) return null;

        Dictionary<string, object?>? state = null;
        for (int i = 0; i < GenAiTagNames.Count; i++)
        {
            var name = GenAiTagNames[i];
            var value = activity.GetTagItem(name);
            if (value is null) continue;
            state ??= new Dictionary<string, object?>(GenAiTagNames.Count);
            state[name] = value;
        }
        return state is null ? null : logger.BeginScope(state);
    }

    /// <summary>
    /// Returns a snapshot of every <c>gen_ai.*</c> tag currently set on
    /// <paramref name="activity"/>. Intended for tests and diagnostics; the
    /// hot logging path should call <see cref="BeginGenAiScope"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Snapshot(Activity? activity)
    {
        if (activity is null) return EmptySnapshot;
        Dictionary<string, object?>? state = null;
        for (int i = 0; i < GenAiTagNames.Count; i++)
        {
            var name = GenAiTagNames[i];
            var value = activity.GetTagItem(name);
            if (value is null) continue;
            state ??= new Dictionary<string, object?>(GenAiTagNames.Count);
            state[name] = value;
        }
        return state ?? EmptySnapshot;
    }

    static readonly IReadOnlyDictionary<string, object?> EmptySnapshot =
        new Dictionary<string, object?>();
}

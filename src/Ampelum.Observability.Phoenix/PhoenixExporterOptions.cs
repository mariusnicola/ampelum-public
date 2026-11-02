using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.Phoenix;

/// <summary>Tunable options for <see cref="PhoenixExporter"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PhoenixExporterOptions
{
    /// <summary>Emit OpenInference / Arize alias attributes alongside the standard <c>gen_ai.*</c> names.</summary>
    public bool EmitOpenInferenceAliases { get; set; } = true;

    /// <summary>When <see langword="true"/>, emits user/assistant message events on the span (vs. attributes).</summary>
    public bool IncludeAuditPayload { get; set; }
}

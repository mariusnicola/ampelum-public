using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>SSE <c>event:</c> field constants emitted by host streaming endpoints. Spec §7.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class SseEventNames
{
    /// <summary>Streaming token delta.</summary>
    public const string Token    = "token";
    /// <summary>Accumulated partial output for one field.</summary>
    public const string Partial  = "partial";
    /// <summary>
    /// Final event for both forward and optimize streams.
    /// Forward stream payload type: <see cref="SseFinalPayload"/>.
    /// Optimize stream payload type: <c>PromptVersion</c> (per spec §7).
    /// </summary>
    public const string Final    = "final";
    /// <summary>Mid-stream error; always followed by <see cref="Done"/>.</summary>
    public const string Error    = "error";
    /// <summary>Terminal event; always last in any stream.</summary>
    public const string Done     = "done";
    /// <summary>Optimizer epoch progress.</summary>
    public const string Progress = "progress";
    /// <summary>Individual optimizer trial result.</summary>
    public const string Trial    = "trial";

    /// <summary>Event names emitted by <c>POST /modules/{moduleName}/stream</c>.</summary>
    public static class Forward
    {
        /// <summary>All forward-stream event names in emission order.</summary>
        public static IReadOnlyList<string> All { get; } = new[] { Token, Partial, Final, Error, Done };
    }

    /// <summary>Event names emitted by <c>GET /optimize/runs/{runId}/stream</c>.</summary>
    public static class Optimize
    {
        /// <summary>All optimize-stream event names in emission order.</summary>
        public static IReadOnlyList<string> All { get; } = new[] { Progress, Trial, Final, Error, Done };
    }
}

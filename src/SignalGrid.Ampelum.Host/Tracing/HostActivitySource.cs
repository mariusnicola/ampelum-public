using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Tracing;

/// <summary>Shared <see cref="System.Diagnostics.ActivitySource"/> for all Ampelum Host telemetry spans.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class HostActivitySource
{
    /// <summary>The activity source name used when registering with OpenTelemetry.</summary>
    public const string Name    = "SignalGrid.Ampelum.Host";
    /// <summary>The semantic version emitted on each activity.</summary>
    public const string Version = "0.7.0";
    /// <summary>The shared <see cref="System.Diagnostics.ActivitySource"/> instance.</summary>
    public static readonly ActivitySource Source = new(Name, Version);

    /// <summary>Well-known operation names used as activity display names for each endpoint.</summary>
    public static class EndpointNames
    {
        /// <summary>Synchronous forward (predict) endpoint.</summary>
        public const string Forward        = "forward";
        /// <summary>Streaming SSE forward endpoint.</summary>
        public const string Stream         = "stream";
        /// <summary>List all registered modules.</summary>
        public const string ModulesList    = "modules.list";
        /// <summary>Get a single registered module descriptor.</summary>
        public const string ModuleGet      = "modules.get";
        /// <summary>Read audit log entries.</summary>
        public const string AuditRead      = "audit.read";
        /// <summary>Verify the audit chain integrity.</summary>
        public const string AuditVerify    = "audit.verify";
        /// <summary>Get a single audit entry by sequence.</summary>
        public const string AuditGet       = "audit.get";
        /// <summary>Replay an audit entry through the pipeline.</summary>
        public const string AuditReplay    = "audit.replay";
        /// <summary>List all stored prompt versions.</summary>
        public const string PromptsList    = "prompts.list";
        /// <summary>List prompt versions for a specific module.</summary>
        public const string PromptsModule  = "prompts.module";
        /// <summary>Get the currently active prompt version for a module.</summary>
        public const string PromptsActive  = "prompts.active";
        /// <summary>Create a new prompt version.</summary>
        public const string PromptsCreate  = "prompts.create";
        /// <summary>Promote a prompt version to active.</summary>
        public const string PromptsPromote = "prompt.promote";
        /// <summary>Start an optimizer run.</summary>
        public const string OptimizeStart  = "optimize.start";
        /// <summary>Check optimizer run status.</summary>
        public const string OptimizeStatus = "optimize.status";
        /// <summary>Stream optimizer progress via SSE.</summary>
        public const string OptimizeStream = "optimize.stream";
        /// <summary>Liveness health check.</summary>
        public const string Health         = "health";
        /// <summary>Readiness health check.</summary>
        public const string Ready          = "ready";
        /// <summary>OpenAPI document endpoint.</summary>
        public const string OpenApi        = "openapi";
        /// <summary>Admin index rebuild endpoint.</summary>
        public const string AdminRebuild   = "admin.rebuild";

        /// <summary>All known endpoint operation names, for bulk ActivitySource registration.</summary>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            Forward, Stream, ModulesList, ModuleGet, AuditRead, AuditVerify, AuditGet,
            AuditReplay, PromptsList, PromptsModule, PromptsActive, PromptsCreate,
            PromptsPromote, OptimizeStart, OptimizeStatus, OptimizeStream, Health, Ready,
            OpenApi, AdminRebuild,
        };
    }
}

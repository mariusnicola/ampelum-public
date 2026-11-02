using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host;

/// <summary>Top-level configuration for the Ampelum host server.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AmpelumHostOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "Host";

    /// <summary>Storage paths (SQLite, audit, prompts).</summary>
    public StorageOptions Storage { get; init; } = new();

    /// <summary>OpenRouter LLM gateway settings.</summary>
    public OpenRouterOptions OpenRouter { get; init; } = new();

    /// <summary>Module registry settings (reserved for future use).</summary>
    public ModulesOptions Modules { get; init; } = new();

    /// <summary>Server-sent events tuning.</summary>
    public SseOptions Sse { get; init; } = new();

    /// <summary>Audit chain settings.</summary>
    public AuditOptions Audit { get; init; } = new();

    /// <summary>When true, admin-only endpoints (/admin/*) are mapped.</summary>
    public bool AdminEndpointsEnabled { get; init; } = false;

    /// <summary>TCP port the host listens on.</summary>
    public int Port { get; init; } = 5170;

    /// <summary>Full URL the host listens on (used for Kestrel binding).</summary>
    public string Url { get; init; } = "http://localhost:5170";

    /// <summary>Storage path configuration.</summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public sealed class StorageOptions
    {
        /// <summary>Path to the SQLite database file.</summary>
        public string SqlitePath { get; init; } = "./data/host.db";

        /// <summary>Path to the JSONL audit log file.</summary>
        public string AuditFile { get; init; } = "./data/audit.jsonl";

        /// <summary>Directory containing prompt version files.</summary>
        public string PromptDir { get; init; } = "./data/prompts";
    }

    /// <summary>OpenRouter LLM gateway configuration.</summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public sealed class OpenRouterOptions
    {
        /// <summary>OpenRouter API key (set via env var or user secrets).</summary>
        public string ApiKey { get; init; } = "";

        /// <summary>Default model to use when none is specified per-request. Empty until configured.</summary>
        public string DefaultModel { get; init; } = string.Empty;
    }

    /// <summary>Module registry configuration (reserved; v0.7 ignores).</summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public sealed class ModulesOptions
    {
        /// <summary>Path to the module registry JSON file (reserved for future use).</summary>
        public string RegistryFile { get; init; } = "./modules.json";
    }

    /// <summary>SSE streaming tuning options.</summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public sealed class SseOptions
    {
        /// <summary>Interval in seconds between SSE keep-alive comments.</summary>
        public int KeepAliveSeconds { get; init; } = 15;

        /// <summary>Maximum milliseconds to wait for the first LLM token before a timeout error.</summary>
        public int FirstTokenDeadlineMs { get; init; } = 1500;
    }

    /// <summary>Audit chain configuration.</summary>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public sealed class AuditOptions
    {
        /// <summary>Expected schema version of the audit chain (1 or 2).</summary>
        public int ChainSchemaVersion { get; init; } = 2;
    }
}

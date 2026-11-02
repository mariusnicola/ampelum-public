using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Full definition of a single agent tool — corresponds to one YAML manifest file.</summary>
public sealed record ToolManifest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required DangerTier DangerTier { get; init; }
    public required Reversibility Reversibility { get; init; }
    public bool RequiresConfirmation { get; init; }

    /// <summary>YAML-defined args schema (JSON Schema). Null = no args.</summary>
    public JsonElement? ArgsSchema { get; init; }

    /// <summary>Allowed values for enum-style args. Keyed by arg name.</summary>
    public IReadOnlyDictionary<string, string[]>? AllowedValues { get; init; }

    /// <summary>Failure handling strategy. Default: abort.</summary>
    public OnFailureMode OnFailure { get; init; } = OnFailureMode.Abort;

    /// <summary>Maximum retries when OnFailure = Retry. Irrelevant otherwise.</summary>
    public int MaxRetries { get; init; } = 3;

    // ---- Observation Token fields ----

    /// <summary>If set, this tool issues observation tokens on successful execution.
    /// Safe/Additive read/list tools issue tokens (e.g., read_file, list_directory).</summary>
    public ObservationSource? IssuesObservationToken { get; init; }

    /// <summary>If set, this tool requires an observation token to execute.
    /// ToolDispatcher validates the token before dispatching.
    /// For Mutating+ tiers with no explicit requirement, ToolLoader applies safe default.</summary>
    public ObservationRequirement? RequiresObservationToken { get; init; }

    /// <summary>Controlled-vocabulary exemption from observation requirement.
    /// Only valid for Mutating+ tools. Free text rejected at load time.</summary>
    public ObservationExemption? ObservationExemption { get; init; }

    /// <summary>For AtomicReadWrite exemption — which arg performs the internal read.
    /// ToolLoader validates this field is set when exemption is AtomicReadWrite.</summary>
    public string? AtomicReadArg { get; init; }

    /// <summary>For ExternalSystemBootstrap exemption — one-sentence justification, audited.</summary>
    public string? BootstrapJustification { get; init; }
}

/// <summary>Survey-level summary — returned by IToolLoader.SurveyAsync.
/// Intentionally minimal to keep token footprint low.</summary>
public sealed record ToolSurvey
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required DangerTier DangerTier { get; init; }
    public bool RequiresConfirmation { get; init; }
}

public enum OnFailureMode
{
    Abort,
    Continue,
    Retry
}
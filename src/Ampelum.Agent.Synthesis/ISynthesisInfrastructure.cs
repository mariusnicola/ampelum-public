using SignalGrid.Ampelum.Agent;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>Shared infrastructure for both WorkflowSynthesizer and SkillSynthesizer.
/// Owns: LLM round-trip (via Ampelum Predict), validate-retry loop, version store, file I/O.</summary>
public interface ISynthesisInfrastructure
{
    /// <summary>Run generate → validate → retry loop. Returns validated artifact content.
    /// Max retries: 3. Validator returns ValidationResult with Errors list.</summary>
    Task<SynthesisResult> GenerateAndValidateAsync(
        string prompt,
        string? existingContent,
        Func<string, string[]> validator,
        CancellationToken ct = default);

    /// <summary>Write artifact to canonical path. Archive prior version to .history/. </summary>
    Task<string> WriteVersionedAsync(
        string canonicalPath,
        string content,
        string version,
        CancellationToken ct = default);

    /// <summary>Read execution metrics from sidecar file. Returns null if no sidecar exists.</summary>
    Task<WorkflowMetrics?> ReadMetricsAsync(string canonicalPath, CancellationToken ct = default);

    /// <summary>Update metrics sidecar atomically (temp file + rename, no partial-write corruption).</summary>
    Task WriteMetricsAsync(string canonicalPath, WorkflowMetrics metrics, CancellationToken ct = default);
}

/// <summary>Result of the generate → validate → retry loop.</summary>
public sealed record SynthesisResult(
    string Content,
    bool Valid,
    int AttemptsRequired,
    IReadOnlyList<string> ValidationErrors
);

/// <summary>Metrics sidecar — the "accident" to the YAML's "substance".
/// Updated after each workflow run. Atomic write (temp file + rename).</summary>
public sealed record WorkflowMetrics
{
    public required string WorkflowName { get; init; }
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
    public long RunCount { get; init; }
    public double SuccessRate { get; init; }
    public double AvgDurationSeconds { get; init; }
    public double P95DurationSeconds { get; init; }
    public DateTimeOffset LastRunAt { get; init; }
    public bool LastRunSucceeded { get; init; }
}

/// <summary>Bridges Ampelum core types into Synthesis for assembly-level dependency.
/// Exists solely to force the Ampelum.dll reference from the Synthesis package.</summary>
public sealed class SynthesisBridge
{
    /// <summary>The Ampelum core Predict static factory — proves we reference Ampelum.dll.</summary>
    public static readonly Type AmpelumPredictType = typeof(SignalGrid.Ampelum.Predict);

    /// <summary>The Agent Identity type — proves we reference Ampelum.Agent.dll.</summary>
    public static readonly Type AgentIdentityType = typeof(AgentIdentity);
}
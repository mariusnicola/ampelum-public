namespace SignalGrid.Ampelum.Agent;

/// <summary>A quality gate that returns a deterministic pass/fail judgment.
/// No LLM involvement — gates are script/command-based or rule-based checks.</summary>
public interface IQualityGate
{
    /// <summary>Unique identifier for this gate (e.g., "build", "test", "lint", "peer-review").</summary>
    string Id { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>Run the gate against the given workspace root.
    /// Returns a deterministic result — never LLM-dependent.</summary>
    Task<QualityGateResult> RunAsync(string workspaceRoot, CancellationToken ct = default);
}

/// <summary>Result of running a quality gate. Domain-agnostic — no .NET-specific fields.</summary>
public sealed record QualityGateResult(
    string GateId,
    bool Success,
    string? Output,         // stdout or summary text
    int? FindingsCount,     // null if not applicable (e.g., binary pass/fail)
    TimeSpan Duration,
    string? Error = null
);
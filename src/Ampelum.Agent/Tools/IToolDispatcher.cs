using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Dispatches a tool invocation through the full policy + audit pipeline.</summary>
public interface IToolDispatcher
{
    /// <summary>Invoke a tool by name. Resolves secret-ref args, evaluates policy,
    /// dispatches to loaded implementation, and records audit entry.</summary>
    Task<ToolResult> InvokeAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> args,
        AgentIdentity caller,
        CancellationToken ct = default);

    /// <summary>Invokes with runtime correlation and an optional approved
    /// confirmation. Implementations that execute real tools must persist the
    /// correlation in their governance audit.</summary>
    Task<ToolResult> InvokeAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> args,
        AgentIdentity caller,
        ToolInvocationContext context,
        CancellationToken ct = default) => InvokeAsync(toolName, args, caller, ct);
}

/// <summary>Correlation supplied by a workflow/runtime when it invokes a tool.
/// Both values are recorded for every governance event.  When omitted, the
/// dispatcher creates one correlation id and uses it as both trace and workflow
/// id so an invocation is never unaudited or uncorrelated.</summary>
public sealed record ToolInvocationContext(
    string? TraceId = null,
    string? WorkflowId = null,
    string? ConfirmationId = null,
    string? InvocationId = null);

/// <summary>Result of a single tool invocation. <see cref="ObservationTokenId"/> carries the
/// observation token id issued or consumed during this invocation, or null if the tool neither
/// issues nor requires one. When a single invocation both consumes a required token and issues
/// a new one, this holds the issued token id (the consumed id is still visible on the
/// corresponding audit entry).</summary>
public sealed record ToolResult(
    string ToolName,
    bool Success,
    string? Output,
    string? Error,
    DateTimeOffset ExecutedAt,
    TimeSpan Duration,
    string? ObservationTokenId = null
)
{
    /// <summary>Create a ToolResult without specifying ToolName, ExecutedAt, Duration (for tests).
    /// Uses empty toolname, current time, and zero duration.</summary>
    public ToolResult(bool success, string? output, string? error, PolicyDecision? _)
        : this(
            "",
            success,
            output,
            error,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero)
    {
    }
}

/// <summary>Audit log for tool invocations — append-only. Entries are never
/// mutated or deleted after being recorded, but the log does not currently
/// implement a cryptographic hash chain between entries.</summary>
public interface IToolAuditLog
{
    Task RecordAsync(ToolAuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<ToolAuditEntry>> QueryAsync(string? toolName = null, int limit = 100, CancellationToken ct = default);
}

/// <summary>Audit record for a single tool invocation.
/// Per-invocation: includes args (as aliases, never raw values), policy decision, and outcome.
/// Entries are append-only and ordered by <see cref="Sequence"/>, but there is no
/// cryptographic hash chain linking an entry to its predecessor.</summary>
public sealed record ToolAuditEntry(
    string AuditId,
    string ToolName,
    string ActorId,
    string ActorType,
    IReadOnlyDictionary<string, object?> Args,
    PolicyDecision PolicyDecision,
    ToolResult? Result,
    DateTimeOffset Timestamp,
    string? TraceId = null,
    string? WorkflowId = null,
    string EventType = "invocation.completed",
    string? ConfirmationId = null,
    string? ObservationTokenId = null,
    long Sequence = 0
);

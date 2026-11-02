namespace SignalGrid.Ampelum.Agent;

/// <summary>Proof that a specific resource was observed in the current session.
/// Issued by ToolDispatcher when a Safe/Additive read tool executes successfully.
/// Validated centrally before any mutating tool executes — handler never sees it.</summary>
public sealed record ObservationToken(
    string TokenId,            // UUID — unique per issuance
    string ResourcePath,       // the exact resource that was observed
    string IssuedByTool,       // e.g. "read_file", "list_directory"
    string SessionId,          // AgentIdentity.InstanceId — token is session-scoped
    int IssuedAtCallIndex,     // ToolDispatcher call counter at issuance
    int ExpiresAtCallIndex     // IssuedAtCallIndex + N (configurable per DangerTier)
);

/// <summary>Types of observation tokens.</summary>
public enum TokenType
{
    /// <summary>Token from a read operation (e.g., read_file). Binds to a specific path.</summary>
    ReadToken,

    /// <summary>Token from a listing operation (e.g., list_directory, pm2_list).
    /// Covers all resources visible in that listing.</summary>
    ListToken,
}

/// <summary>Stores and resolves observation tokens for the current session.</summary>
public interface ITokenStore
{
    /// <summary>Store a newly issued token.</summary>
    void Store(ObservationToken token);

    /// <summary>Resolve a token by its TokenId string. Returns null if not found.</summary>
    ObservationToken? Resolve(string tokenId);

    /// <summary>Purge all tokens for the current session.</summary>
    void PurgeSession();
}

/// <summary>Stable authority boundary for durable observation tokens.</summary>
public sealed record ObservationTokenScope(string WorkspaceId, string SessionId, string IssuerId)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(IssuerId);
    }
}

/// <summary>Correlation carried into the token audit trail.</summary>
public sealed record ObservationTokenAuditContext(string TraceId, string WorkflowId, string ActorId)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TraceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ActorId);
    }
}

/// <summary>Durable lifecycle entry for an observation token.</summary>
public sealed record ObservationTokenAuditEvent(
    long Sequence,
    string TokenId,
    string EventType,
    DateTimeOffset OccurredAt,
    ObservationTokenScope Scope,
    ObservationTokenAuditContext Context,
    string? Reason = null);

/// <summary>Result of an atomic durable token consumption attempt.</summary>
public sealed record ObservationTokenConsumptionResult(bool Consumed, string? RejectionReason = null);

/// <summary>Optional extension for durable stores. Consumption is atomic and
/// one-time, so callers cannot validate then replay a token in another process.</summary>
public interface IDurableObservationTokenStore : ITokenStore
{
    void Store(ObservationToken token, ObservationTokenScope scope, DateTimeOffset expiresAt, ObservationTokenAuditContext context);

    ObservationTokenConsumptionResult Consume(string tokenId, ObservationTokenScope scope, string? resourcePath, ObservationTokenAuditContext context);

    bool Revoke(string tokenId, ObservationTokenScope scope, string reason, ObservationTokenAuditContext context);

    Task<IReadOnlyList<ObservationTokenAuditEvent>> QueryAuditAsync(string? tokenId = null, CancellationToken ct = default);
}

/// <summary>Thrown when a mutating tool is invoked without a required observation token.</summary>
public sealed class MissingObservationTokenException : InvalidOperationException
{
    public string ToolName { get; }
    public string ArgName { get; }

    public MissingObservationTokenException(string toolName, string argName)
        : base($"Tool '{toolName}' requires observation token parameter '{argName}'.")
    {
        ToolName = toolName;
        ArgName = argName;
    }
}

/// <summary>Declares that a tool issues observation tokens on successful execution.</summary>
public sealed record ObservationSource
{
    /// <summary>Type of token this tool issues.</summary>
    public required TokenType TokenType { get; init; }

    /// <summary>Name of the arg that contains the resource path.
    /// Null for listing tools that cover all resources (e.g., pm2_list).</summary>
    public string? ResourcePathArg { get; init; }
}

/// <summary>Declares that a tool requires an observation token to execute.</summary>
public sealed record ObservationRequirement
{
    /// <summary>Parameter name the LLM passes for the token.</summary>
    public required string ArgName { get; init; }

    /// <summary>Accepted token types. Empty = any type accepted.</summary>
    public TokenType[] AcceptedTypes { get; init; } = [];

    /// <summary>Name of the arg in this tool that must match the token's ResourcePath.
    /// Null = no path binding needed (list token).</summary>
    public string? ResourcePathArg { get; init; }

    /// <summary>For write_file — verify that the target path was NOT in the listing.
    /// Only valid when AcceptedTypes contains ListToken.</summary>
    public string? VerifyPathAbsent { get; init; }
}

/// <summary>Controlled vocabulary for observation exemptions.
/// Free text values are rejected at load time.</summary>
public enum ObservationExemption
{
    /// <summary>Creates a resource that does not yet exist.
    /// No arg may reference an existing file path.</summary>
    NewResourceCreation,

    /// <summary>Tool reads and writes in a single atomic operation.
    /// Tool must declare atomicReadArg pointing to its internal read.</summary>
    AtomicReadWrite,

    /// <summary>First-time setup of an external system (CI, cloud resource).
    /// Requires bootstrapJustification field (one sentence, audited).</summary>
    ExternalSystemBootstrap,
}

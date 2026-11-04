using SignalGrid.Ampelum.Agent.A2A;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Warning raised during capability verification of an agent card.</summary>
public sealed record CapabilityWarning(string CapabilityName, string Warning);

/// <summary>Decision for A2A task delegation trust evaluation.</summary>
public enum TrustDecision
{
    /// <summary>Task delegation is permitted.</summary>
    Allow,

    /// <summary>Task delegation requires confirmation from a trusted actor.</summary>
    RequireConfirmation,

    /// <summary>Task delegation is blocked.</summary>
    Block,
}

/// <summary>Verifies incoming A2A agent identities via JWT + capability claims.</summary>
public interface IAgentCardVerifier
{
    // Existing methods (backward compat)
    Task<AgentIdentity?> VerifyAsync(string agentCardJwt, CancellationToken ct = default);
    Task<CapabilityVerification> VerifyCapabilitiesAsync(
        AgentIdentity identity,
        IReadOnlyList<string> requiredCapabilities,
        CancellationToken ct = default);

    // New spec-compliant methods
    /// <summary>Verify the JWT in the agent card and return the verified identity, or null if invalid.</summary>
    Task<AgentIdentity?> VerifyIdentityAsync(A2AAgentCard card, CancellationToken ct = default);

    /// <summary>Evaluate capability claims in the agent card against known-safe schemas.
    /// Returns warnings for unrecognized capability shapes.</summary>
    IReadOnlyList<CapabilityWarning> VerifyCapabilities(A2AAgentCard card);
}

/// <summary>Result of capability verification.</summary>
public sealed record CapabilityVerification(
    bool AllRequiredPresent,
    IReadOnlyList<string> Warnings
);

/// <summary>Policy for evaluating A2A task delegation trust decisions.</summary>
public interface IA2ATrustPolicy
{
    /// <summary>Evaluate whether a task delegation to an external agent is permitted.</summary>
    TrustDecision EvaluateDelegation(
        A2AAgentCard targetCard,
        AgentIdentity? verifiedIdentity,
        TrustLevel currentTrust);
}

/// <summary>Plugin task executor for A2A — delegates tasks to external agents.</summary>
public interface IA2ATaskExecutor : ITaskExecutor
{
    new string TaskType { get; }
    new Task<TaskExecutionResult> ExecuteAsync(TaskNode node, CancellationToken ct = default);
}

/// <summary>Failure modes for A2A delegation.</summary>
public enum A2AFailureMode
{
    Unreachable,
    Partial,
    TimedOut,
    SchemaMismatch,
    IdentityFailure,
}

/// <summary>Default implementation of IAgentCardVerifier that validates
/// A2A agent cards against the spec and extracts identity claims.</summary>
public sealed class DefaultAgentCardVerifier : IAgentCardVerifier
{
    private readonly DefaultAgentIdentityProvider _issuer;

    public DefaultAgentCardVerifier(DefaultAgentIdentityProvider issuer)
    {
        _issuer = issuer;
    }

    /// <summary>Verify an A2A agent card JWT. Returns null if invalid or expired.</summary>
    public async Task<AgentIdentity?> VerifyAsync(string agentCardJwt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(agentCardJwt))
            return null;

        try
        {
            return await _issuer.VerifyAsync(agentCardJwt, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Verify that an agent identity has the required capabilities (scope entries).
    /// Returns warnings for any missing capabilities.</summary>
    public Task<CapabilityVerification> VerifyCapabilitiesAsync(
        AgentIdentity identity,
        IReadOnlyList<string> requiredCapabilities,
        CancellationToken ct = default)
    {
        var available = new HashSet<string>(identity.Scope);
        var missing = requiredCapabilities
            .Where(c => !available.Contains(c))
            .ToList();

        return Task.FromResult(new CapabilityVerification(
            missing.Count == 0,
            missing.Select(m => $"Required capability '{m}' not present in agent scope").ToList()
        ));
    }

    /// <summary>Verify the JWT in the agent card and return the verified identity, or null if invalid.</summary>
    public async Task<AgentIdentity?> VerifyIdentityAsync(A2AAgentCard card, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);

        // Extract JWT from bearer authentication scheme if present
        var bearerScheme = card.AuthenticationSchemes?
            .FirstOrDefault(s => string.Equals(s.Scheme, "bearer", StringComparison.OrdinalIgnoreCase));

        if (bearerScheme?.Credentials == null)
            return null;

        // A valid token is not enough: it has to be the token of the agent the card describes.
        var identity = await VerifyAsync(bearerScheme.Credentials, ct);
        return identity is not null && string.Equals(identity.AgentType, card.Name, StringComparison.OrdinalIgnoreCase)
            ? identity
            : null;
    }

    /// <summary>Evaluate capability claims in the agent card against known-safe schemas.
    /// Returns warnings for unrecognized capability shapes.</summary>
    public IReadOnlyList<CapabilityWarning> VerifyCapabilities(A2AAgentCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var warnings = new List<CapabilityWarning>();

        // The known shape is the four flags. Anything an agent puts in Custom is outside it, and the caller decides
        // what to do with that, so each such entry is reported by its key.
        foreach (var capability in card.Capabilities ?? [])
        {
            foreach (var key in capability?.Custom?.Keys ?? [])
                warnings.Add(new CapabilityWarning(key, $"'{key}' is not a known capability (streaming, push notifications, state sync, long running tasks)"));
        }

        return warnings;
    }
}

/// <summary>Default implementation of IA2ATaskExecutor that uses
/// A2AClient to delegate tasks to external A2A agents.</summary>
public sealed class DefaultA2ATaskExecutor : IA2ATaskExecutor
{
    private readonly A2AClient _client;

    public string TaskType { get; } = "a2a/external";

    public DefaultA2ATaskExecutor() : this(new A2AClient()) { }
    public DefaultA2ATaskExecutor(A2AClient client)
    {
        _client = client;
    }

    /// <summary>Execute a task by delegating to a remote A2A agent.
    /// Expects node.Metadata["agentUrl"] to contain the remote agent's endpoint URL.</summary>
    public async Task<TaskExecutionResult> ExecuteAsync(TaskNode node, CancellationToken ct = default)
    {
        if (node.Metadata is null || !node.Metadata.TryGetValue("agentUrl", out var urlObj) || urlObj is not string agentUrl)
        {
            return new TaskExecutionResult
            {
                Success = false,
                Error = "Missing 'agentUrl' in task metadata — cannot delegate A2A task",
            };
        }

        try
        {
            var message = new A2AMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = MessageRole.Agent,
                Parts = new[]
                {
                    new A2APart { Text = node.Description },
                },
            };

            var response = await _client.SendMessageAsync(agentUrl, message, ct: ct);

            if (response.Error is not null)
            {
                return new TaskExecutionResult
                {
                    Success = false,
                    Error = $"A2A remote error [{response.Error.Code}]: {response.Error.Message}",
                };
            }

            if (response.Result?.Task is null)
            {
                return new TaskExecutionResult
                {
                    Success = false,
                    Error = "A2A response missing task result",
                };
            }

            var output = string.Join("\n", response.Result.Task.Messages
                .SelectMany(m => m.Parts)
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrEmpty(t)));

            return new TaskExecutionResult
            {
                Success = response.Result.Task.State == TaskState.Completed,
                Output = string.IsNullOrEmpty(output) ? null : output,
                Error = response.Result.Task.State == TaskState.Failed
                    ? response.Result.Task.Status?.Message ?? "A2A task failed"
                    : null,
            };
        }
        catch (HttpRequestException ex)
        {
            return new TaskExecutionResult
            {
                Success = false,
                Error = $"A2A HTTP request failed: {ex.Message}",
            };
        }
    }

    Task<TaskExecutionResult> ITaskExecutor.ExecuteAsync(TaskNode node, CancellationToken ct)
        => ExecuteAsync(node, ct);
}

/// <summary>Default implementation of IA2ATrustPolicy for A2A task delegation.
/// Uses trust levels to determine whether external agent delegation is permitted.</summary>
public sealed class DefaultA2ATrustPolicy : IA2ATrustPolicy
{
    /// <summary>Evaluate whether a task delegation to an external agent is permitted.
    ///
    /// Decision matrix:
    /// - verifiedIdentity == null → Block
    /// - currentTrust == TrustLevel.None → Block
    /// - currentTrust == TrustLevel.Unknown → RequireConfirmation
    /// - currentTrust >= TrustLevel.Provisional → Allow
    /// </summary>
    public TrustDecision EvaluateDelegation(
        A2AAgentCard targetCard,
        AgentIdentity? verifiedIdentity,
        TrustLevel currentTrust)
    {
        ArgumentNullException.ThrowIfNull(targetCard);

        // Identity verification failure → block immediately
        if (verifiedIdentity == null)
            return TrustDecision.Block;

        // No trust → block
        if (currentTrust == TrustLevel.None)
            return TrustDecision.Block;

        // Unknown trust → require confirmation
        if (currentTrust == TrustLevel.Unknown)
            return TrustDecision.RequireConfirmation;

        // Provisional or higher → allow
        if (currentTrust >= TrustLevel.Provisional)
            return TrustDecision.Allow;

        // Fallback (should not reach)
        return TrustDecision.Block;
    }
}
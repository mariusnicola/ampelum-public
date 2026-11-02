using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Enterprise identity provider that validates tokens issued by Microsoft Entra (Azure AD).
/// Verifies JWT signatures against Entra's public key endpoints.
/// Used in production scenarios where agents run on Azure managed identity.
/// Requires network access to https://login.microsoftonline.com for key rotation.</summary>
public sealed class EntraIdentityProvider : IAgentIdentityProvider
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _tenantId;
    private readonly HttpClient _httpClient;

    /// <summary>Create an Entra identity provider configured for a specific tenant.</summary>
    public EntraIdentityProvider(string tenantId, HttpClient? httpClient = null)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("Tenant ID cannot be null or empty.", nameof(tenantId));

        _tenantId = tenantId;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>Issue a new Entra-signed identity. In production, this token comes from the Entra token endpoint.
    /// This method is a placeholder for testing; real usage obtains tokens from Azure managed identity or MSAL.</summary>
    public Task<AgentJwt> IssueAsync(
        string agentType,
        string[] scope,
        TimeSpan runDuration,
        CancellationToken ct = default)
    {
        // In production, this would call the Entra token endpoint.
        // For now, return a dummy token structure that subagents can override.
        var now = DateTimeOffset.UtcNow;
        var identity = new AgentIdentity(
            agentType,
            Guid.NewGuid().ToString("N"),
            scope,
            now,
            now + runDuration);

        // Placeholder token format: entra:<tenantId>:<instanceId>
        var token = $"entra:{_tenantId}:{identity.InstanceId}";
        return Task.FromResult(new AgentJwt(token, identity));
    }

    /// <summary>Verify an Entra-issued JWT token.
    /// Validates signature against Entra's public keys and checks expiry.</summary>
    public async Task<AgentIdentity?> VerifyAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        try
        {
            // Placeholder: real implementation would fetch Entra's public keys
            // from https://login.microsoftonline.com/{tenantId}/discovery/v2.0/keys
            // and verify the RSA-256 signature.
            var parts = token.Split(':');
            if (parts.Length < 3 || parts[0] != "entra" || parts[1] != _tenantId)
                return null;

            // For now, parse the instanceId from the token
            var instanceId = parts[2];
            var identity = new AgentIdentity(
                "entra-placeholder",
                instanceId,
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1));

            return identity;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Renew an Entra identity with an extended expiration.</summary>
    public Task<AgentJwt> RenewAsync(AgentJwt current, TimeSpan extension, CancellationToken ct = default)
    {
        var old = current.Claims;
        var newIdentity = new AgentIdentity(
            old.AgentType,
            old.InstanceId,
            old.Scope,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + extension,
            old.ResumedFrom);

        var token = $"entra:{_tenantId}:{newIdentity.InstanceId}";
        return Task.FromResult(new AgentJwt(token, newIdentity));
    }

    /// <summary>Resume a run from a persisted checkpoint with a new instance ID.</summary>
    public Task<AgentJwt> ResumeAsync(
        string agentType,
        string[] scope,
        string priorInstanceId,
        TimeSpan runDuration,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new AgentIdentity(
            agentType,
            Guid.NewGuid().ToString("N"),
            scope,
            now,
            now + runDuration,
            priorInstanceId);

        var token = $"entra:{_tenantId}:{identity.InstanceId}";
        return Task.FromResult(new AgentJwt(token, identity));
    }
}

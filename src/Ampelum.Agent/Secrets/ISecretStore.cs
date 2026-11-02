namespace SignalGrid.Ampelum.Agent;

/// <summary>Stores and resolves secrets for agent sessions.
/// Session isolation: secrets are namespaced by AgentIdentity.InstanceId (jti).
/// Secret values are NEVER exposed in LLM context, audit logs, or tool results.</summary>
public interface ISecretStore
{
    /// <summary>Store a secret under an alias, scoped to the caller's session (jti).</summary>
    Task StoreAsync(string alias, string value, string jti, CancellationToken ct = default);

    /// <summary>Resolve a secret by alias for the caller's session.
    /// Throws SecretNotFoundException if the alias doesn't exist — hard failure.</summary>
    Task<string> ResolveAsync(string alias, string jti, CancellationToken ct = default);

    /// <summary>Check if an alias exists for the caller's session.</summary>
    Task<bool> ExistsAsync(string alias, string jti, CancellationToken ct = default);

    /// <summary>List all aliases for the caller's session. Never returns values.</summary>
    Task<IReadOnlyList<string>> ListAliasesAsync(string jti, CancellationToken ct = default);

    /// <summary>Delete a secret by alias. Throws SecretNotFoundException if not found.</summary>
    Task DeleteAsync(string alias, string jti, CancellationToken ct = default);

    /// <summary>Purge all secrets for a session. Called when session ends or expires.</summary>
    Task PurgeSessionAsync(string jti, CancellationToken ct = default);
}

/// <summary>Thrown when a secret alias cannot be resolved. Hard failure — escalation required.</summary>
public sealed class SecretNotFoundException : InvalidOperationException
{
    public string Alias { get; }
    public string Jti { get; }

    public SecretNotFoundException(string alias, string jti)
        : base($"Secret '{alias}' not found for session '{jti}'.")
    {
        Alias = alias;
        Jti = jti;
    }
}

/// <summary>Thrown when the secret store is not writable (e.g., env-var backend).</summary>
public sealed class SecretStoreReadOnlyException : NotSupportedException
{
    public SecretStoreReadOnlyException(string backend)
        : base($"Secret store backend '{backend}' is read-only.") { }
}
namespace SignalGrid.Ampelum.Agent;

/// <summary>Read-only secret store backed by environment variables.
/// Env var naming: <c>AMPL_SECRET_{alias}</c>.
/// Session scope (jti) is ignored since env vars are process-wide.</summary>
public sealed class EnvironmentSecretStore : ISecretStore
{
    internal const string Prefix = "AMPL_SECRET_";

    /// <summary>Reads the env var <c>AMPL_SECRET_{alias}</c> and returns its value.
    /// Throws <see cref="SecretNotFoundException"/> if not set.</summary>
    public Task<string> ResolveAsync(string alias, string jti, CancellationToken ct = default)
    {
        var value = Environment.GetEnvironmentVariable(Prefix + alias);
        if (value is null)
            throw new SecretNotFoundException(alias, jti);

        return Task.FromResult(value);
    }

    /// <summary>Checks whether the env var <c>AMPL_SECRET_{alias}</c> is set.</summary>
    public Task<bool> ExistsAsync(string alias, string jti, CancellationToken ct = default)
    {
        var exists = Environment.GetEnvironmentVariable(Prefix + alias) is not null;
        return Task.FromResult(exists);
    }

    /// <summary>Lists all env vars starting with <c>AMPL_SECRET_</c>,
    /// returning the suffix (alias) for each.</summary>
    public Task<IReadOnlyList<string>> ListAliasesAsync(string jti, CancellationToken ct = default)
    {
        var aliases = new List<string>();
        var envVars = Environment.GetEnvironmentVariables();
        foreach (var key in envVars.Keys)
        {
            var keyStr = key as string;
            if (keyStr is not null && keyStr.StartsWith(Prefix, StringComparison.Ordinal))
                aliases.Add(keyStr[Prefix.Length..]);
        }

        return Task.FromResult<IReadOnlyList<string>>(aliases);
    }

    /// <summary>Not supported. Throws <see cref="SecretStoreReadOnlyException"/>.</summary>
    public Task StoreAsync(string alias, string value, string jti, CancellationToken ct = default)
        => throw new SecretStoreReadOnlyException("environment");

    /// <summary>Not supported. Throws <see cref="SecretStoreReadOnlyException"/>.</summary>
    public Task DeleteAsync(string alias, string jti, CancellationToken ct = default)
        => throw new SecretStoreReadOnlyException("environment");

    /// <summary>No-op — environment variables are process-wide and cannot be
    /// purged per session.</summary>
    public Task PurgeSessionAsync(string jti, CancellationToken ct = default)
        => Task.CompletedTask;
}

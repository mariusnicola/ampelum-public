using System.Collections.Concurrent;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Thread-safe in-memory implementation of ISecretStore.
/// Stores secrets scoped to session (jti). For testing and single-node deployments.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _sessions = new(StringComparer.Ordinal);
    private readonly bool _readOnly;

    public InMemorySecretStore(bool readOnly = false)
    {
        _readOnly = readOnly;
    }

    public Task StoreAsync(string alias, string value, string jti, CancellationToken ct = default)
    {
        if (_readOnly)
            throw new SecretStoreReadOnlyException("memory");

        var session = _sessions.GetOrAdd(jti, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        session[alias] = value;
        return Task.CompletedTask;
    }

    public Task<string> ResolveAsync(string alias, string jti, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(jti, out var session) && session.TryGetValue(alias, out var value))
            return Task.FromResult(value);

        throw new SecretNotFoundException(alias, jti);
    }

    public Task<bool> ExistsAsync(string alias, string jti, CancellationToken ct = default)
    {
        var exists = _sessions.TryGetValue(jti, out var session) && session.ContainsKey(alias);
        return Task.FromResult(exists);
    }

    public Task<IReadOnlyList<string>> ListAliasesAsync(string jti, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(jti, out var session))
            return Task.FromResult<IReadOnlyList<string>>(new List<string>());

        var aliases = new List<string>(session.Keys);
        return Task.FromResult<IReadOnlyList<string>>(aliases.AsReadOnly());
    }

    public Task DeleteAsync(string alias, string jti, CancellationToken ct = default)
    {
        if (_readOnly)
            throw new SecretStoreReadOnlyException("memory");

        if (!_sessions.TryGetValue(jti, out var session) || !session.TryRemove(alias, out _))
            throw new SecretNotFoundException(alias, jti);

        return Task.CompletedTask;
    }

    public Task PurgeSessionAsync(string jti, CancellationToken ct = default)
    {
        _sessions.TryRemove(jti, out _);
        return Task.CompletedTask;
    }
}
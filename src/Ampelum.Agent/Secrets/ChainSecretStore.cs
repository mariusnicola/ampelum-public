namespace SignalGrid.Ampelum.Agent;

/// <summary>Composite secret store that chains multiple backends.
/// Write operations go to the primary (first) store only.
/// Read operations try each store in order, returning the first hit.
/// Delete and purge propagate to all stores.</summary>
public sealed class ChainSecretStore : ISecretStore
{
    private readonly IReadOnlyList<ISecretStore> _stores;

    public ChainSecretStore(IReadOnlyList<ISecretStore> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Count == 0)
            throw new ArgumentException("At least one store is required.", nameof(stores));

        _stores = stores;
    }

    /// <summary>Tries each store in order, returning the value from the first
    /// that resolves. Throws <see cref="SecretNotFoundException"/> if all miss.</summary>
    public async Task<string> ResolveAsync(string alias, string jti, CancellationToken ct = default)
    {
        SecretNotFoundException? lastException = null;

        foreach (var store in _stores)
        {
            try
            {
                return await store.ResolveAsync(alias, jti, ct).ConfigureAwait(false);
            }
            catch (SecretNotFoundException ex)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new SecretNotFoundException(alias, jti);
    }

    /// <summary>Checks each store in order, returning true on the first hit.</summary>
    public async Task<bool> ExistsAsync(string alias, string jti, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            if (await store.ExistsAsync(alias, jti, ct).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    /// <summary>Writes to the primary (first) store only.</summary>
    public Task StoreAsync(string alias, string value, string jti, CancellationToken ct = default)
        => _stores[0].StoreAsync(alias, value, jti, ct);

    /// <summary>Deletes from every store in the chain.
    /// Aggregates exceptions; throws <see cref="SecretNotFoundException"/> if
    /// none of the stores had the alias.</summary>
    public async Task DeleteAsync(string alias, string jti, CancellationToken ct = default)
    {
        var anyDeleted = false;
        var exceptions = new List<Exception>();

        foreach (var store in _stores)
        {
            try
            {
                await store.DeleteAsync(alias, jti, ct).ConfigureAwait(false);
                anyDeleted = true;
            }
            catch (SecretNotFoundException)
            {
                // Some stores may not have this alias — that's fine
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        if (!anyDeleted)
            throw new SecretNotFoundException(alias, jti);

        if (exceptions.Count > 0)
            throw new AggregateException(
                $"One or more stores failed during delete of '{alias}'.", exceptions);
    }

    /// <summary>Purges the session from every store in the chain.</summary>
    public async Task PurgeSessionAsync(string jti, CancellationToken ct = default)
    {
        foreach (var store in _stores)
            await store.PurgeSessionAsync(jti, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the union of aliases from all stores, deduplicated.</summary>
    public async Task<IReadOnlyList<string>> ListAliasesAsync(string jti, CancellationToken ct = default)
    {
        var combined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in _stores)
        {
            var aliases = await store.ListAliasesAsync(jti, ct).ConfigureAwait(false);
            foreach (var alias in aliases)
                combined.Add(alias);
        }

        return combined.ToList().AsReadOnly();
    }
}

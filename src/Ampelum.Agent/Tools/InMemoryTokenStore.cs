using System.Collections.Concurrent;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Thread-safe in-memory implementation of <see cref="ITokenStore"/>.
/// Stores observation tokens in a <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// keyed by TokenId. Tokens are scoped per session — PurgeSession removes all
/// tokens for the current session.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly ConcurrentDictionary<string, ObservationToken> _tokens = new(StringComparer.Ordinal);

    /// <summary>Thread-local call index counter, incremented on each token issuance.</summary>
    private int _callIndex;

    /// <summary>Gets the current call index (number of tokens issued so far).</summary>
    public int CallIndex => Volatile.Read(ref _callIndex);

    /// <inheritdoc />
    public void Store(ObservationToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _tokens[token.TokenId] = token;
    }

    /// <inheritdoc />
    public ObservationToken? Resolve(string tokenId)
    {
        ArgumentNullException.ThrowIfNull(tokenId);
        return _tokens.TryGetValue(tokenId, out var token) ? token : null;
    }

    /// <inheritdoc />
    public void PurgeSession()
    {
        // Remove all tokens. Since tokens are session-scoped via SessionId,
        // purging all is equivalent to purging the current session.
        _tokens.Clear();
    }

    /// <summary>Generate the next call index and return it.
    /// Called by ToolDispatcher on each token issuance.</summary>
    public int NextCallIndex() => Interlocked.Increment(ref _callIndex);

    /// <summary>Remove a specific token (e.g., after single-use consumption).</summary>
    public bool Consume(string tokenId) => _tokens.TryRemove(tokenId, out _);

    /// <summary>Get all tokens currently in the store. For testing and diagnostics.</summary>
    public IReadOnlyList<ObservationToken> GetAllTokens() => [.. _tokens.Values];
}

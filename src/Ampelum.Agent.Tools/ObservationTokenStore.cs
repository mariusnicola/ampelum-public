using System.Collections.Concurrent;

namespace SignalGrid.Ampelum.Agent.Tools;

/// <summary>A proof that a resource was observed before mutation.</summary>
public sealed record ObservationToken(
    string TokenId,
    string ResourcePath,
    int ExpiresAtCallIndex);

/// <summary>Thread-safe store for ObservationTokens, scoped to one agent session.</summary>
public sealed class ObservationTokenStore
{
    readonly ConcurrentDictionary<string, ObservationToken> _tokens = new(StringComparer.Ordinal);
    int _callIndex;

    /// <summary>Issues a token for the given resource path. Expires after N calls.</summary>
    public ObservationToken Issue(string path, int expiryAfterCalls = 10)
    {
        var token = new ObservationToken(
            Guid.NewGuid().ToString("N")[..12],
            Canonicalize(path),
            _callIndex + expiryAfterCalls);
        _tokens[token.TokenId] = token;
        return token;
    }

    /// <summary>Validates a token for the given target path. Increments call counter on use.</summary>
    public bool Validate(string tokenId, string targetPath)
    {
        _callIndex++;
        if (!_tokens.TryGetValue(tokenId, out var token))
            return false;
        if (Canonicalize(targetPath) != token.ResourcePath)
            return false;
        if (_callIndex > token.ExpiresAtCallIndex)
            return false;
        return true;
    }

    static string Canonicalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}

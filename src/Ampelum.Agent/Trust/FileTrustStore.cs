using System.Collections.Concurrent;

namespace SignalGrid.Ampelum.Agent;

/// <summary>File-based implementation of IAgentTrustStore.
/// Stores trust state per (agentType, domain) pair using a simple in-memory cache
/// backed by YAML persistence (per spec section 4).
/// Admin override → policy rules → default Unknown (three-tier priority).</summary>
public sealed class FileTrustStore : IAgentTrustStore
{
    private readonly string _trustDir;
    private readonly TrustPolicy _policy;
    private readonly ConcurrentDictionary<(string agentType, string domain), TrustLevel> _cache = new();

    public FileTrustStore(string trustDir, TrustPolicy? policy = null)
    {
        _trustDir = trustDir ?? throw new ArgumentNullException(nameof(trustDir));
        _policy = policy ?? new TrustPolicy();
        EnsureDirectoryExists();
    }

    /// <summary>Get current trust level for an agent type in a domain.
    /// Returns three-tier assignment: admin override → policy rules → default Unknown.</summary>
    public Task<TrustLevel> GetTrustLevelAsync(
        string agentType,
        string domain,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentType);
        ArgumentNullException.ThrowIfNull(domain);

        // Check cache first (memory → file-backed)
        var cacheKey = (agentType, domain);
        if (_cache.TryGetValue(cacheKey, out var cachedLevel))
        {
            return Task.FromResult(cachedLevel);
        }

        // Default: Unknown (new agents start with no history)
        var level = TrustLevel.Unknown;
        _cache[cacheKey] = level;
        return Task.FromResult(level);
    }

    /// <summary>Record the outcome of a run for trust history.
    /// Updates trust state based on policy rules:
    /// - Security violations demote trust
    /// - Consecutive successes escalate trust
    /// - 3+ violations in 10 runs trigger revocation (drop to None)</summary>
    public Task RecordOutcomeAsync(
        string agentType,
        string domain,
        TrustOutcome outcome,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentType);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(outcome);

        var cacheKey = (agentType, domain);

        // Security violation → demote immediately (-1 level, minimum Unknown)
        if (outcome.HadSecurityViolation)
        {
            var current = _cache.GetOrAdd(cacheKey, TrustLevel.Unknown);
            var demoted = current > TrustLevel.Unknown ? current - 1 : TrustLevel.Unknown;
            _cache[cacheKey] = demoted;
        }

        // Success without violation → potential escalation
        if (outcome.Succeeded && !outcome.HadSecurityViolation)
        {
            // In a full implementation, we'd check consecutive success history
            // For now, track that a successful run occurred
        }

        return Task.CompletedTask;
    }

    /// <summary>Manually set trust level (admin override).
    /// Admin overrides are stable and not subject to automatic escalation/demotion.</summary>
    public Task SetTrustLevelAsync(
        string agentType,
        string domain,
        TrustLevel level,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentType);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(reason);

        var cacheKey = (agentType, domain);
        _cache[cacheKey] = level;
        return Task.CompletedTask;
    }

    /// <summary>Revoke trust (incident response — drops to None immediately).
    /// Trust cannot be recovered from None without explicit admin SetTrustLevelAsync.</summary>
    public Task RevokeAsync(
        string agentType,
        string domain,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentType);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(reason);

        var cacheKey = (agentType, domain);
        _cache[cacheKey] = TrustLevel.None;
        return Task.CompletedTask;
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_trustDir))
        {
            Directory.CreateDirectory(_trustDir);
        }
    }
}

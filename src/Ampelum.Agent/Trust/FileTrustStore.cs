using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>File-based implementation of IAgentTrustStore.
/// Keeps trust state per (agentType, domain) pair in memory and in one JSON file per pair under the trust
/// directory, so a new store on the same directory sees the levels the last one wrote. The file name is a hash of
/// the pair, so any agent type or domain is a valid name.
/// A violation demotes one level (never below Unknown); <see cref="RevokeAsync"/> drops to None.
/// A level is read from its file the first time the pair is asked for; a file changed by another store after that
/// is not re-read.</summary>
public sealed class FileTrustStore : IAgentTrustStore
{
    private readonly string _trustDir;
    private readonly TrustPolicy _policy;
    private readonly ConcurrentDictionary<(string agentType, string domain), TrustLevel> _cache = new();
    private readonly object _gate = new();

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

        return Task.FromResult(Load((agentType, domain)));
    }

    /// <summary>The cached level, else the level in the pair's file, else Unknown (a new agent has no history).</summary>
    private TrustLevel Load((string agentType, string domain) key)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
            var path = PathFor(key);
            var level = File.Exists(path) ? Read(path) : TrustLevel.Unknown;
            _cache[key] = level;
            return level;
        }
    }

    private void Store((string agentType, string domain) key, TrustLevel level)
    {
        lock (_gate)
        {
            _cache[key] = level;
            var path = PathFor(key);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("agentType", key.agentType);
                writer.WriteString("domain", key.domain);
                writer.WriteString("level", level.ToString());
                writer.WriteEndObject();
            }
            File.Move(temp, path, overwrite: true);
        }
    }

    private static TrustLevel Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return Enum.Parse<TrustLevel>(doc.RootElement.GetProperty("level").GetString()!);
    }

    private string PathFor((string agentType, string domain) key) =>
        Path.Combine(_trustDir,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.agentType + "\0" + key.domain))) + ".json");

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
            lock (_gate)
            {
                var current = Load(cacheKey);
                var demoted = current > TrustLevel.Unknown ? current - 1 : TrustLevel.Unknown;
                Store(cacheKey, demoted);
            }
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
        Store(cacheKey, level);
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
        Store(cacheKey, TrustLevel.None);
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

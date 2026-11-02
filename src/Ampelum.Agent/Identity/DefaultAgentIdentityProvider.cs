using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Default HMAC-based agent identity provider.
/// Signs JWTs with a caller-supplied symmetric key.
/// Suitable for local/dev scenarios. Production should use asymmetric keys
/// backed by ISecretStore.</summary>
public sealed class DefaultAgentIdentityProvider : IAgentIdentityProvider
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly byte[] _signingKey;

    /// <summary>Creates a provider that signs and verifies with <paramref name="signingKey"/>.</summary>
    /// <param name="signingKey">HMAC key, at least 32 bytes, supplied from configuration.</param>
    /// <exception cref="ArgumentException">The key is null or shorter than 32 bytes.</exception>
    public DefaultAgentIdentityProvider(byte[] signingKey)
    {
        if (signingKey is null || signingKey.Length < 32)
            throw new ArgumentException("Signing key must be at least 32 bytes.", nameof(signingKey));
        _signingKey = (byte[])signingKey.Clone();
    }

    /// <summary>Issue a new signed JWT identity for an agent run.</summary>
    public Task<AgentJwt> IssueAsync(
        string agentType,
        string[] scope,
        TimeSpan runDuration,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new AgentIdentity(
            agentType,
            Guid.NewGuid().ToString("N"),
            scope,
            now,
            now + runDuration);

        var token = SignToken(identity);
        return Task.FromResult(new AgentJwt(token, identity));
    }

    /// <summary>Verify and parse an inbound JWT token.</summary>
    public Task<AgentIdentity?> VerifyAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
            return Task.FromResult<AgentIdentity?>(null);

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
                return Task.FromResult<AgentIdentity?>(null);

            // Verify signature
            var header = parts[0];
            var payload = parts[1];
            var sigBase64 = parts[2];

            var expectedSig = ComputeHmac($"{header}.{payload}");
            var actualSig = Base64UrlDecode(sigBase64);

            if (!ConstantTimeEquals(expectedSig, actualSig))
                return Task.FromResult<AgentIdentity?>(null);

            // Parse payload
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payload));
            var dto = JsonSerializer.Deserialize<IdentityDto>(payloadJson, _json);
            if (dto is null)
                return Task.FromResult<AgentIdentity?>(null);

            var identity = dto.ToIdentity();
            if (identity.ExpiresAt < DateTimeOffset.UtcNow)
                return Task.FromResult<AgentIdentity?>(null);

            return Task.FromResult<AgentIdentity?>(identity);
        }
        catch
        {
            return Task.FromResult<AgentIdentity?>(null);
        }
    }

    /// <summary>Renew an identity with an extended expiration.</summary>
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

        var token = SignToken(newIdentity);
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

        var token = SignToken(identity);
        return Task.FromResult(new AgentJwt(token, identity));
    }

    // ── Signing ────────────────────────────────────────────────────

    private string SignToken(AgentIdentity identity)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(
            """{"alg":"HS256","typ":"JWT"}"""));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(IdentityDto.FromIdentity(identity), _json)));
        var sig = Base64UrlEncode(ComputeHmac($"{header}.{payload}"));
        return $"{header}.{payload}.{sig}";
    }

    private byte[] ComputeHmac(string input)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
    }

    // ── DTO ────────────────────────────────────────────────────────

    private sealed record IdentityDto
    {
        public string AgentType { get; init; } = string.Empty;
        public string InstanceId { get; init; } = string.Empty;
        public string[] Scope { get; init; } = [];
        public DateTimeOffset IssuedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public string? ResumedFrom { get; init; }

        public static IdentityDto FromIdentity(AgentIdentity id) => new()
        {
            AgentType = id.AgentType,
            InstanceId = id.InstanceId,
            Scope = id.Scope,
            IssuedAt = id.IssuedAt,
            ExpiresAt = id.ExpiresAt,
            ResumedFrom = id.ResumedFrom,
        };

        public AgentIdentity ToIdentity() => new(
            AgentType, InstanceId, Scope, IssuedAt, ExpiresAt, ResumedFrom);
    }

    // ── Base64URL helpers ──────────────────────────────────────────

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var result = 0;
        for (var i = 0; i < a.Length; i++)
            result |= a[i] ^ b[i];
        return result == 0;
    }
}
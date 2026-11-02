namespace SignalGrid.Ampelum.Agent;

/// <summary>Verifies tool manifests loaded from external sources before registration.
/// Built-in sources (YamlDirectoryToolSource) are implicitly trusted.
/// MCP and remote sources are verified before use.
/// Rejected tools are blocked at load time, not at call time.</summary>
public interface IToolManifestVerifier
{
    /// <summary>Verify a tool manifest loaded from an external source.
    /// If verification fails, the tool is rejected at load time.</summary>
    /// <param name="manifest">The tool manifest to verify.</param>
    /// <param name="sourcePrefix">Unique identifier for the source (e.g., "mcp://anthropic/resources", "remote://registry.example.com").</param>
    /// <returns>Verification result with trusted status and assigned trust tier.</returns>
    VerificationResult Verify(ToolManifest manifest, string sourcePrefix);
}

/// <summary>Result of tool manifest verification — includes trust status and assigned tier.
/// Uses the existing TrustLevel enum from agent trust system.</summary>
public sealed record VerificationResult(
    bool Trusted,
    TrustLevel AssignedTier,
    string? Reason = null
);

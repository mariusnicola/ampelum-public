using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Mcp;

public sealed class McpToolSourceTests
{
    private sealed class StubVerifier : IToolManifestVerifier
    {
        private readonly bool _trusted;

        public StubVerifier(bool trusted = true)
        {
            _trusted = trusted;
        }

        public VerificationResult Verify(ToolManifest manifest, string sourcePrefix)
        {
            if (_trusted)
            {
                return new VerificationResult(Trusted: true, AssignedTier: TrustLevel.Trusted);
            }
            return new VerificationResult(Trusted: false, AssignedTier: TrustLevel.None, Reason: "Failed verification");
        }
    }

    // ── Tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterManifest_AddsManifestToSurvey()
    {
        var connection = new McpServerConnection("https://api.github.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection);

        var manifest = new ToolManifest
        {
            Name = "list_repos",
            Description = "List GitHub repositories",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible,
            RequiresConfirmation = false
        };

        var registered = source.RegisterManifest(manifest);

        registered.ShouldBeTrue();
        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);
        surveys.Count.ShouldBe(1);
        surveys[0].Name.ShouldBe("list_repos");
    }

    [Fact]
    public async Task RegisterManifest_WithFailingVerifier_Rejected()
    {
        var connection = new McpServerConnection("https://api.github.com", TrustLevel.Trusted);
        var verifier = new StubVerifier(trusted: false);
        var source = new McpToolSource(connection, verifier);

        var manifest = new ToolManifest
        {
            Name = "suspicious_tool",
            Description = "A suspicious tool",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible,
            RequiresConfirmation = false
        };

        var registered = source.RegisterManifest(manifest);

        registered.ShouldBeFalse();
        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);
        surveys.Count.ShouldBe(0);
    }

    [Fact]
    public void SourcePrefix_DerivedFromServerUri()
    {
        var connection = new McpServerConnection("https://api.github.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection);

        source.SourcePrefix.ShouldBe("mcp/api-github-com");
    }

    [Fact]
    public void SourcePrefix_HandlesComplexUri()
    {
        var connection = new McpServerConnection("https://registry.example.com:8080/path", TrustLevel.Trusted);
        var source = new McpToolSource(connection);

        source.SourcePrefix.ShouldBe("mcp/registry-example-com");
    }

    [Fact]
    public void ConnectionTrust_ReturnsAssignedTrust()
    {
        var connection = new McpServerConnection("https://api.github.com", TrustLevel.Established);
        var source = new McpToolSource(connection);

        source.ConnectionTrust.ShouldBe(TrustLevel.Established);
    }

    [Fact]
    public async Task TryUnpackAsync_ReturnsManifest_IfRegistered()
    {
        var connection = new McpServerConnection("https://api.github.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection);

        var manifest = new ToolManifest
        {
            Name = "get_user",
            Description = "Get GitHub user info",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible,
            RequiresConfirmation = false
        };

        source.RegisterManifest(manifest);
        var unpacked = await source.TryUnpackAsync("get_user", TestContext.Current.CancellationToken);

        unpacked.ShouldNotBeNull();
        unpacked!.Name.ShouldBe("get_user");
    }

    [Fact]
    public async Task TryUnpackAsync_ReturnsNull_IfNotRegistered()
    {
        var connection = new McpServerConnection("https://api.github.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection);

        var unpacked = await source.TryUnpackAsync("nonexistent", TestContext.Current.CancellationToken);

        unpacked.ShouldBeNull();
    }
}

using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

/// <summary>Tests for ToolDispatcher accepting optional convention enforcer parameter.</summary>
public sealed class ToolDispatcherPreWriteHookTests
{
    private static readonly DateTimeOffset TestNow = DateTimeOffset.UtcNow;

    [Fact]
    public void Constructor_AcceptsConventionEnforcer()
    {
        // Arrange
        var loader = new InMemoryToolLoader();
        var conventionManifest = new ConventionManifest { Version = 1, Preset = "ampelum" };
        var enforcer = new DefaultConventionEnforcer(conventionManifest);

        // Act: create dispatcher with convention enforcer
        var dispatcher = new ToolDispatcher(
            loader,
            new InMemoryTokenStore(),
            new PolicyEngine(),
            new AgentIdentity("test", "session-id", Array.Empty<string>(), TestNow, TestNow.AddHours(1)),
            conventionEnforcer: enforcer);

        // Assert: dispatcher created successfully with enforcer
        dispatcher.ShouldNotBeNull();
    }

    [Fact]
    public void Constructor_AllowsNullConventionEnforcer()
    {
        // Arrange & Act
        var dispatcher = new ToolDispatcher(
            new InMemoryToolLoader(),
            new InMemoryTokenStore(),
            new PolicyEngine(),
            new AgentIdentity("test", "session-id", Array.Empty<string>(), TestNow, TestNow.AddHours(1)),
            conventionEnforcer: null);

        // Assert: dispatcher created successfully without enforcer
        dispatcher.ShouldNotBeNull();
    }
}

/// <summary>Minimal in-memory tool loader for testing.</summary>
internal sealed class InMemoryToolLoader : IToolLoader
{
    private readonly Dictionary<string, ToolManifest> _manifests = new();

    public void Register(ToolManifest manifest) => _manifests[manifest.Name] = manifest;

    public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
    {
        var surveys = _manifests.Values.Select(m => new ToolSurvey
        {
            Name = m.Name,
            Description = m.Description,
            DangerTier = m.DangerTier,
            RequiresConfirmation = m.RequiresConfirmation,
        }).ToList();
        return Task.FromResult<IReadOnlyList<ToolSurvey>>(surveys);
    }

    public Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default)
    {
        return Task.FromResult<ToolManifest>(_manifests.TryGetValue(toolName, out var m) ? m : throw new KeyNotFoundException($"Tool '{toolName}' not found."));
    }

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
    {
        return Task.FromResult<ToolManifest?>(_manifests.TryGetValue(toolName, out var m) ? m : null);
    }

    public ValueTask EvictAsync(string toolName)
    {
        return default;
    }

    public Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default)
    {
        return SurveyAsync(ct);
    }
}

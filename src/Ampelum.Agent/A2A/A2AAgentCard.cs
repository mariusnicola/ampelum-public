namespace SignalGrid.Ampelum.Agent.A2A;

/// <summary>A2A agent discovery card (served at GET /.well-known/agent-card).</summary>
public sealed record A2AAgentCard
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Url { get; init; }
    public string? Provider { get; init; }
    public string? ProviderUrl { get; init; }
    public IReadOnlyList<A2AAgentSkill>? Skills { get; init; }
    public IReadOnlyList<A2AAgentCapability>? Capabilities { get; init; }
    public IReadOnlyList<A2AAuthenticationScheme>? AuthenticationSchemes { get; init; }
    public IReadOnlyDictionary<string, object?>? CustomExtensions { get; init; }
}

/// <summary>A skill exposed by an A2A agent.</summary>
public sealed record A2AAgentSkill
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyDictionary<string, object?>? InputSchema { get; init; }
    public IReadOnlyDictionary<string, object?>? OutputSchema { get; init; }
}

/// <summary>Agent capability flags for A2A protocol features.</summary>
public sealed record A2AAgentCapability
{
    public bool Streaming { get; init; }
    public bool PushNotifications { get; init; }
    public bool StateSync { get; init; }
    public bool LongRunningTasks { get; init; }
    public IReadOnlyDictionary<string, object?>? Custom { get; init; }
}

/// <summary>Authentication scheme for connecting to an A2A agent.</summary>
public sealed record A2AAuthenticationScheme
{
    public required string Scheme { get; init; }  // "bearer", "oauth2", "mtls", etc
    public string? Credentials { get; init; }
    public IReadOnlyDictionary<string, object?>? Config { get; init; }
}
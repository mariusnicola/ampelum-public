namespace SignalGrid.Ampelum.Agent.A2A;

/// <summary>Role of the message sender in an A2A conversation.</summary>
public enum MessageRole
{
    User = 0,
    Agent = 1,
}

/// <summary>Content part of an A2A message.</summary>
public sealed record A2APart
{
    public string? Text { get; init; }
    public A2AFileContent? File { get; init; }
    public A2ADataContent? Data { get; init; }
    public string? Id { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>A file reference within an A2A part.</summary>
public sealed record A2AFileContent
{
    public required string Name { get; init; }
    public required string MimeType { get; init; }
    public required string Bytes { get; init; }  // base64-encoded
}

/// <summary>Structured data within an A2A part (JSON-like).</summary>
public sealed record A2ADataContent
{
    public required IReadOnlyDictionary<string, object?> Data { get; init; }
}

/// <summary>A message in an A2A task conversation.</summary>
public sealed record A2AMessage
{
    public string Jsonrpc { get; init; } = "2.0";
    public required string Id { get; init; }
    public required MessageRole Role { get; init; }
    public required IReadOnlyList<A2APart> Parts { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
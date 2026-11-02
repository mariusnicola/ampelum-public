namespace SignalGrid.Ampelum.Agent.A2A;

/// <summary>A2A task state machine states per spec v1.0.</summary>
public enum TaskState
{
    Submitted = 0,
    Working = 1,
    InputRequired = 2,
    Completed = 3,
    Canceled = 4,
    Failed = 5,
    Unknown = 6,
}

/// <summary>Status detail for completed/failed tasks.</summary>
public sealed record TaskStatus
{
    public required string Code { get; init; }
    public string? Message { get; init; }
}

/// <summary>An A2A task — the unit of work between agents.</summary>
public sealed record A2ATask
{
    public required string Id { get; init; }
    public required TaskState State { get; init; }
    public string? ContextId { get; init; }
    public required MessageCollection Messages { get; init; }
    public IReadOnlyList<A2AArtifact>? Artifacts { get; init; }
    public TaskStatus? Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? HistoryLength { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>An artifact produced by a task (output files, data, etc).</summary>
public sealed record A2AArtifact
{
    public required string Index { get; init; }
    public required A2APart Parts { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>A collection of messages (backed by IReadOnlyList).</summary>
public sealed record MessageCollection : IReadOnlyList<A2AMessage>
{
    private readonly IReadOnlyList<A2AMessage> _messages;
    public MessageCollection(IReadOnlyList<A2AMessage> messages) => _messages = messages;
    public A2AMessage this[int index] => _messages[index];
    public int Count => _messages.Count;
    public IEnumerator<A2AMessage> GetEnumerator() => _messages.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _messages.GetEnumerator();
}
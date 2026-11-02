namespace SignalGrid.Ampelum.Agent;

/// <summary>Unique identifier for a workspace within a portfolio.</summary>
public readonly record struct WorkspaceId(string Value)
{
    public static WorkspaceId New(string name) =>
        new(name.ToLowerInvariant().Replace(' ', '-'));

    public override string ToString() => Value;
}
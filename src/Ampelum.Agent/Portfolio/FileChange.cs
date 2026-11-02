namespace SignalGrid.Ampelum.Agent;

/// <summary>A single file change — path and unified diff.</summary>
public sealed record FileChange(string Path, string Diff);

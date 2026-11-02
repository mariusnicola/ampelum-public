namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>Type of relationship between two code symbols.</summary>
public enum EdgeType
{
    Call,
    Extends,
    Implements,
    References,
    Imports
}

/// <summary>A directed edge between two code symbols.</summary>
public sealed record CodeGraphEdge(
    string SrcFile,
    int SrcLine,
    string SrcName,
    string? TgtFile,
    int? TgtLine,
    string TgtName,
    EdgeType EdgeType,
    int? SourceLine = null);

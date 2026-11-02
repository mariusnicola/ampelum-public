namespace Ampelum.Samples.BulkDocuments;

/// <summary>One document to summarize. The id doubles as the batch custom id.</summary>
public sealed record Document(string Id, string Title, string Text);

/// <summary>Outcome for one document of a batch: a summary, or the last error the provider reported.</summary>
public sealed record DocumentSummary(string DocumentId, string? Summary, string? Error);

/// <summary>Which strategy picks the endpoint for an interactive request.</summary>
public enum RoutingMode
{
    Failover,
    LoadBalanced,
    KeyRotating,
}

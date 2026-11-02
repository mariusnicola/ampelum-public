namespace SignalGrid.Ampelum.Context.Retrieval;

/// <summary>A single event retrieved by the IRetriever with relevance score.</summary>
public sealed record RetrievedSnippet(
    string EventKey,
    string Body,
    float Score,
    int Rank);

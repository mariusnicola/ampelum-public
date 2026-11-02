using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Loaders;

/// <summary>Thrown when an <see cref="IDocumentLoader"/> reads more bytes than <see cref="DocumentLoadContext.MaxDocumentBytes"/> permits.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LoaderBudgetExceededException(long limit, long actual)
    : InvalidOperationException($"Loader exceeded MaxDocumentBytes={limit} (actual={actual}).")
{
    /// <summary>Configured limit (bytes).</summary>
    public long Limit { get; } = limit;

    /// <summary>Actual size observed (bytes).</summary>
    public long Actual { get; } = actual;
}

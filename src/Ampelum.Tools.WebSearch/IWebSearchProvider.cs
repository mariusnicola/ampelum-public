namespace SignalGrid.Ampelum.Tools.WebSearch;

/// <summary>Pluggable web-search backend used by <see cref="WebSearchTool"/>.</summary>
public interface IWebSearchProvider
{
    /// <summary>Issues a query and returns up to <paramref name="topK"/> results.</summary>
    /// <param name="query">User query string.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct);
}

/// <summary>A single web-search hit.</summary>
/// <param name="Title">Result title.</param>
/// <param name="Url">Result URL.</param>
/// <param name="Snippet">Short excerpt or description.</param>
public sealed record SearchResult(string Title, string Url, string Snippet);

using SignalGrid.Ampelum.Tools;

namespace SignalGrid.Ampelum.Tools.WebSearch;

/// <summary>
/// ITypedTool wrapping a pluggable <see cref="IWebSearchProvider"/> (default Brave Search).
/// External category — POLC tags `network:egress`, `data:public-internet` (see README).
/// </summary>
[ToolCategory(ToolCategoryKind.External)]
public sealed class WebSearchTool : ITypedTool<IReadOnlyList<SearchResult>>
{
    readonly IWebSearchProvider _provider;

    /// <summary>Creates the tool.</summary>
    /// <param name="provider">The search provider.</param>
    /// <param name="query">User query string.</param>
    /// <param name="topK">Maximum number of results (default 3).</param>
    public WebSearchTool(IWebSearchProvider provider, string query, int topK = 3)
    {
        _provider = provider;
        Query = query;
        TopK = topK;
    }

    /// <inheritdoc/>
    public string Name => "web_search";
    /// <inheritdoc/>
    public string Description => "Searches the public web and returns ranked title/url/snippet hits.";

    /// <summary>The query string to search for.</summary>
    [FreeForm]
    public string Query { get; }

    /// <summary>Number of results to return.</summary>
    public int TopK { get; }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SearchResult>> InvokeAsync(CancellationToken ct)
        => _provider.SearchAsync(Query, TopK, ct);
}

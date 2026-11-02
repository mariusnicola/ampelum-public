using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SignalGrid.Ampelum.Tools;

namespace SignalGrid.Ampelum.Tools.Wikipedia;

/// <summary>
/// ITypedTool: Wikipedia REST summary lookup.
/// Endpoint: <c>https://en.wikipedia.org/api/rest_v1/page/summary/{title}</c>.
/// POLC tags `network:egress`, `data:public-internet`.
/// </summary>
[ToolCategory(ToolCategoryKind.External)]
public sealed class WikipediaTool : ITypedTool<WikiSummary>
{
    readonly HttpClient _http;

    /// <summary>Creates the tool.</summary>
    /// <param name="http"><see cref="HttpClient"/> used for the REST call.</param>
    /// <param name="title">Article title (e.g. <c>"Albert Einstein"</c>).</param>
    public WikipediaTool(HttpClient http, string title)
    {
        _http = http;
        Title = title;
    }

    /// <inheritdoc/>
    public string Name => "wikipedia";
    /// <inheritdoc/>
    public string Description => "Looks up the lead paragraph for an English Wikipedia article.";

    /// <summary>Article title.</summary>
    [FreeForm]
    public string Title { get; }

    /// <inheritdoc/>
    public async ValueTask<WikiSummary> InvokeAsync(CancellationToken ct)
    {
        var uri = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(Title.Replace(' ', '_'))}";
        using var resp = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<WikiPayload>(ct).ConfigureAwait(false);
        return new WikiSummary(
            payload?.Title ?? Title,
            payload?.Extract ?? "",
            payload?.ContentUrls?.Desktop?.Page ?? "");
    }

    sealed class WikiPayload
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("extract")] public string? Extract { get; set; }
        [JsonPropertyName("content_urls")] public WikiUrls? ContentUrls { get; set; }
    }
    sealed class WikiUrls
    {
        [JsonPropertyName("desktop")] public WikiUrl? Desktop { get; set; }
    }
    sealed class WikiUrl
    {
        [JsonPropertyName("page")] public string? Page { get; set; }
    }
}

/// <summary>Wikipedia article summary.</summary>
/// <param name="Title">Article title.</param>
/// <param name="Extract">Lead-paragraph extract.</param>
/// <param name="Url">Canonical article URL.</param>
public sealed record WikiSummary(string Title, string Extract, string Url);

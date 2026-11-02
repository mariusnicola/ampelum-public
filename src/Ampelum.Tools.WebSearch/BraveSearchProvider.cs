using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Tools.WebSearch;

/// <summary>
/// <see cref="IWebSearchProvider"/> backed by the Brave Search REST API.
/// Endpoint: <c>https://api.search.brave.com/res/v1/web/search</c>. Requires header <c>X-Subscription-Token</c>.
/// </summary>
public sealed class BraveSearchProvider : IWebSearchProvider
{
    readonly HttpClient _http;
    readonly string _apiKey;

    /// <summary>Creates the Brave provider.</summary>
    /// <param name="http">An <see cref="HttpClient"/>; pass a typed-client for testing.</param>
    /// <param name="apiKey">Brave API key (header <c>X-Subscription-Token</c>).</param>
    public BraveSearchProvider(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct)
    {
        var uri = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={topK}";
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Add("X-Subscription-Token", _apiKey);
        req.Headers.Add("Accept", "application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<BravePayload>(ct).ConfigureAwait(false);
        var hits = payload?.Web?.Results ?? Array.Empty<BraveHit>();
        var list = new List<SearchResult>(hits.Length);
        for (var i = 0; i < hits.Length && i < topK; i++)
        {
            var h = hits[i];
            list.Add(new SearchResult(h.Title ?? "", h.Url ?? "", h.Description ?? ""));
        }
        return list;
    }

    sealed class BravePayload
    {
        [JsonPropertyName("web")] public BraveWeb? Web { get; set; }
    }
    sealed class BraveWeb
    {
        [JsonPropertyName("results")] public BraveHit[]? Results { get; set; }
    }
    sealed class BraveHit
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>HTTP-backed Cohere rerank client (https://docs.cohere.com/reference/rerank-1).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CohereReranker(HttpClient http, CohereRerankerOptions options) : IReranker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedDocument> candidates,
        int topK,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<RetrievedDocument>();
        }

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.reranker.rerank");
        activity?.SetTag("SignalGrid.Ampelum.reranker.kind", "cohere");

        var payload = new CohereRequest(options.Model, query, candidates.Select(c => c.Text).ToList(), topK);
        using var msg = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CohereResponse>(JsonOpts, ct).ConfigureAwait(false);

        if (body?.Results is null)
        {
            throw new InvalidDataException("The Cohere rerank response has no 'results' list.");
        }

        var results = new List<RetrievedDocument>(body.Results.Count);
        var seen = new HashSet<int>();
        foreach (var r in body.Results.Take(topK))
        {
            if (r?.Index is not { } index)
            {
                throw new InvalidDataException("The Cohere rerank response has a result with no 'index'.");
            }
            if (index < 0 || index >= candidates.Count)
            {
                throw new InvalidDataException($"The Cohere rerank response names index {index}, but there are {candidates.Count} candidates.");
            }
            if (!seen.Add(index))
            {
                throw new InvalidDataException($"The Cohere rerank response names index {index} more than once.");
            }
            results.Add(candidates[index] with { Score = r.RelevanceScore });
        }
        return results;
    }

    private sealed class CohereRequest
    {
        public string Model { get; }
        public string Query { get; }
        public List<string> Documents { get; }
        [JsonPropertyName("top_n")] public int TopN { get; }
        public CohereRequest(string model, string query, List<string> documents, int topN)
            => (Model, Query, Documents, TopN) = (model, query, documents, topN);
    }

    private sealed record CohereResponse(
        [property: JsonPropertyName("results")] List<CohereResultItem> Results);

    private sealed record CohereResultItem(
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}

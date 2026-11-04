using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>HTTP-backed Voyage AI rerank client.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class VoyageReranker(HttpClient http, VoyageRerankerOptions options) : IReranker
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
        activity?.SetTag("SignalGrid.Ampelum.reranker.kind", "voyage");

        var payload = new VoyageRequest(options.Model, query, candidates.Select(c => c.Text).ToList(), topK);
        using var msg = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<VoyageResponse>(JsonOpts, ct).ConfigureAwait(false);

        if (body?.Data is null)
        {
            throw new InvalidDataException("The Voyage rerank response has no 'data' list.");
        }

        var results = new List<RetrievedDocument>(body.Data.Count);
        foreach (var r in body.Data.Take(topK))
        {
            if (r.Index < 0 || r.Index >= candidates.Count)
            {
                throw new InvalidDataException($"The Voyage rerank response names index {r.Index}, but there are {candidates.Count} candidates.");
            }
            results.Add(candidates[r.Index] with { Score = r.RelevanceScore });
        }
        return results;
    }

    private sealed class VoyageRequest
    {
        public string Model { get; }
        public string Query { get; }
        public List<string> Documents { get; }
        [JsonPropertyName("top_k")] public int TopK { get; }
        public VoyageRequest(string model, string query, List<string> documents, int topK)
            => (Model, Query, Documents, TopK) = (model, query, documents, topK);
    }

    private sealed record VoyageResponse(
        [property: JsonPropertyName("data")] List<VoyageResultItem> Data);

    private sealed record VoyageResultItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}

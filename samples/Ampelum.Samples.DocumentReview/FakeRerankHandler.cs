using System.Net;
using System.Text;
using System.Text.Json;

namespace Ampelum.Samples.DocumentReview;

/// <summary>One rerank request the fake received.</summary>
internal sealed record RerankCall(RerankerKind Kind, string? Authorization, string Model, string Query, IReadOnlyList<string> Documents, int Limit);

/// <summary>
/// Plays the Cohere and Voyage rerank endpoints. The two differ in the name of the result limit
/// (top_n against top_k) and of the result list (results against data); this handler requires the
/// right names for the vendor it is asked as. A document scores the share of query terms it contains.
/// </summary>
internal sealed class FakeRerankHandler(string expectedKey) : HttpMessageHandler
{
    readonly List<RerankCall> _calls = [];

    public IReadOnlyList<RerankCall> Calls => _calls;

    /// <summary>Added to every result index, so a nonzero value makes the service name pages that do not exist.</summary>
    public int IndexShift { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var kind = path == FakeData.CohereEndpoint.AbsolutePath ? RerankerKind.Cohere
            : path == FakeData.VoyageEndpoint.AbsolutePath ? RerankerKind.Voyage
            : (RerankerKind?)null;
        if (kind is null || request.Method != HttpMethod.Post)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        if (request.Headers.Authorization?.ToString() != $"Bearer {expectedKey}")
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var root = body.RootElement;
        var query = root.GetProperty("query").GetString() ?? "";
        var documents = root.GetProperty("documents").EnumerateArray().Select(d => d.GetString() ?? "").ToList();
        var limit = root.GetProperty(kind == RerankerKind.Cohere ? "top_n" : "top_k").GetInt32();
        _calls.Add(new RerankCall(kind.Value, request.Headers.Authorization.ToString(), root.GetProperty("model").GetString() ?? "", query, documents, limit));

        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "and").ToArray();
        var ranked = documents
            .Select((text, index) => new { index = index + IndexShift, relevance_score = terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)) / (double)terms.Length })
            .OrderByDescending(r => r.relevance_score)
            .ThenBy(r => r.index)
            .Take(limit)
            .ToList();

        var reply = kind == RerankerKind.Cohere
            ? JsonSerializer.Serialize(new { results = ranked })
            : JsonSerializer.Serialize(new { data = ranked });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
    }
}

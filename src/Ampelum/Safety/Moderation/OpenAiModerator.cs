using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Moderation;

/// <summary>OpenAI moderations API adapter. POSTs to /v1/moderations.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class OpenAiModerator : IOutputModerator
{
    readonly HttpClient _http;
    readonly string _apiKey;
    readonly Uri _endpoint;
    readonly ModerationCache _cache;

    /// <summary>Builds a moderator with the given HTTP client + API key. Endpoint defaults to api.openai.com.</summary>
    public OpenAiModerator(HttpClient http, string apiKey, Uri? endpoint = null, int cacheSize = 1024)
    {
        _http = http;
        _apiKey = apiKey;
        _endpoint = endpoint ?? new Uri("https://api.openai.com/v1/moderations");
        _cache = new ModerationCache(cacheSize);
    }

    /// <inheritdoc/>
    public async ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(text, out var cached)) return cached;

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");
        req.Content = JsonContent.Create(new OpenAiModerationRequest { Input = text },
            options: ModerationJsonContext.Default.Options);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync(
            ModerationJsonContext.Default.OpenAiModerationResponse, cancellationToken).ConfigureAwait(false);
        var first = payload!.Results![0];
        var flagged = first.Flagged;
        var categories = first.Categories!.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();
        var result = new ModerationResult(flagged, categories, Confidence: null);
        _cache.Set(text, result);
        return result;
    }
}

internal sealed class OpenAiModerationRequest
{
    [JsonPropertyName("input")] public string Input { get; set; } = "";
}

internal sealed class OpenAiModerationResponse
{
    [JsonPropertyName("results")] public List<OpenAiModerationResult>? Results { get; set; }
}

internal sealed class OpenAiModerationResult
{
    [JsonPropertyName("flagged")] public bool Flagged { get; set; }
    [JsonPropertyName("categories")] public Dictionary<string, bool>? Categories { get; set; }
}

[JsonSerializable(typeof(OpenAiModerationRequest))]
[JsonSerializable(typeof(OpenAiModerationResponse))]
internal sealed partial class ModerationJsonContext : JsonSerializerContext { }

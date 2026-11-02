using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>
/// Default <see cref="ILangSmithClient"/>. Posts batches to <c>/runs/batch</c>
/// and never embeds the API key in thrown exceptions.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangSmithClient : ILangSmithClient
{
    readonly HttpClient _http;
    readonly LangSmithCredentials _credentials;

    /// <summary>Creates a client bound to <paramref name="http"/> and <paramref name="credentials"/>.</summary>
    public LangSmithClient(HttpClient http, IOptions<LangSmithCredentials> credentials)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        _http = http;
        _credentials = credentials.Value;
        _http.BaseAddress ??= _credentials.Endpoint;
    }

    /// <inheritdoc/>
    public async Task SubmitAsync(IReadOnlyList<LangSmithRun> runs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0) return;

        var batch = new LangSmithRunBatch { Post = runs };
        var json = SerializeBatch(batch);
        using var request = new HttpRequestMessage(HttpMethod.Post, "runs/batch")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", _credentials.ApiKey);
        request.Headers.TryAddWithoutValidation("x-project-name", _credentials.ProjectName);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            // Re-throw without leaking the API key into Data.
            throw new HttpRequestException(
                $"LangSmith ingestion failed for project '{_credentials.ProjectName}'.",
                ex,
                ex.StatusCode);
        }
    }

    static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "v0.9e plan deviation: wire payload includes IReadOnlyDictionary<string, object?>; AOT-safe serialization deferred.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "v0.9e plan deviation: wire payload includes IReadOnlyDictionary<string, object?>; AOT-safe serialization deferred.")]
    internal static string SerializeBatch(LangSmithRunBatch batch) =>
        JsonSerializer.Serialize(batch, SerializerOptions);
}

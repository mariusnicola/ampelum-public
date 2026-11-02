using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Observability;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>
/// Default <see cref="ILangFuseClient"/>. POSTs <c>{"batch":[...]}</c> to
/// <c>/api/public/ingestion</c> with HTTP Basic auth derived from
/// <see cref="LangFuseCredentials.ToBasicAuthHeaderValue"/>. On 401/403 the
/// client trips a 60s circuit breaker and drops subsequent requests until the
/// cooldown expires.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangFuseClient : ILangFuseClient
{
    readonly HttpClient _http;
    readonly LangFuseCredentials _credentials;
    readonly LangFuseExporterOptions _options;
    readonly TimeProvider _time;
    DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    /// <summary>Creates a client.</summary>
    public LangFuseClient(
        HttpClient http,
        IOptions<LangFuseCredentials> credentials,
        IOptions<LangFuseExporterOptions> options)
        : this(http, credentials, options, TimeProvider.System) { }

    internal LangFuseClient(
        HttpClient http,
        IOptions<LangFuseCredentials> credentials,
        IOptions<LangFuseExporterOptions> options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _credentials = credentials.Value;
        _options = options.Value;
        _time = time;
        _http.BaseAddress ??= _credentials.Endpoint;
    }

    /// <inheritdoc/>
    public async Task SubmitAsync(IReadOnlyList<LangFuseEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;

        if (_time.GetUtcNow() < _circuitOpenUntil)
        {
            ExporterDiagnostics.RecordExporterAuthFailure("langfuse");
            return;
        }

        var json = SerializeBatch(new LangFuseIngestionRequest { Batch = events });
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/public/ingestion")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credentials.ToBasicAuthHeaderValue());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _circuitOpenUntil = _time.GetUtcNow() + TimeSpan.FromSeconds(60);
            ExporterDiagnostics.RecordExporterAuthFailure("langfuse");
            throw new HttpRequestException(
                $"LangFuse ingestion auth failed; circuit open for 60s.",
                inner: null,
                response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"LangFuse ingestion failed with status {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }
    }

    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "Wire payload includes IReadOnlyDictionary<string, object?>; AOT-safe serialization deferred (v0.9e).")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Wire payload includes IReadOnlyDictionary<string, object?>; AOT-safe serialization deferred (v0.9e).")]
    internal static string SerializeBatch(LangFuseIngestionRequest batch) =>
        JsonSerializer.Serialize(batch, SerializerOptions);

    static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

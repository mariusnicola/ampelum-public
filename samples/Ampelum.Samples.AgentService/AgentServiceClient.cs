using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using SignalGrid.Ampelum.Host.Contracts;

namespace Ampelum.Samples.AgentService;

/// <summary>Raised when the service answers with a non-success status; carries the service's own error body.</summary>
public sealed class AgentServiceException(HttpStatusCode status, ErrorResponse? error)
    : Exception($"Service answered {(int)status}: {error?.Code} {error?.Message}")
{
    public HttpStatusCode Status { get; } = status;
    public ErrorResponse? Error { get; } = error;
}

/// <summary>One frame of a stream response: the SSE event name and its JSON payload.</summary>
public sealed record StreamFrame(string Event, JsonElement Data);

/// <summary>Calls the Host's HTTP endpoints.</summary>
public sealed class AgentServiceClient(HttpClient http)
{
    public async Task<ModuleListResponse> ListModulesAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync("/modules", AmpelumHostContractsJsonContext.Default.ModuleListResponse, ct))!;

    /// <summary>Calls <c>/modules/{module}/forward</c> with the question and returns the typed response.</summary>
    public Task<ForwardResponse> ForwardAsync(string module, string question, CancellationToken ct = default) =>
        ForwardAsync(module, new Dictionary<string, JsonElement> { ["Question"] = JsonSerializer.SerializeToElement(question) }, ct);

    /// <summary>Calls <c>/modules/{module}/forward</c> with caller-built inputs.</summary>
    public async Task<ForwardResponse> ForwardAsync(
        string module, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"/modules/{module}/forward", new ForwardRequest(inputs), AmpelumHostContractsJsonContext.Default.ForwardRequest, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ForwardResponse, ct))!;
    }

    /// <summary>
    /// Calls <c>/modules/{module}/stream</c> and returns every frame the service sends, in order, to the end of the response.
    /// A <c>partial</c> frame carries a field's text so far, <c>final</c> carries the outputs, <c>done</c> is last.
    /// </summary>
    public async Task<IReadOnlyList<StreamFrame>> StreamAsync(string module, string question, CancellationToken ct = default)
    {
        var inputs = new Dictionary<string, JsonElement> { ["Question"] = JsonSerializer.SerializeToElement(question) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/modules/{module}/stream")
        {
            Content = JsonContent.Create(new ForwardRequest(inputs), AmpelumHostContractsJsonContext.Default.ForwardRequest),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);

        var frames = new List<StreamFrame>();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(ct))
            frames.Add(new StreamFrame(item.EventType, JsonDocument.Parse(item.Data).RootElement.Clone()));
        return frames;
    }

    public async Task<AuditPage> ListAuditAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync("/audit", AmpelumHostContractsJsonContext.Default.AuditPage, ct))!;

    public async Task<AuditVerifyResponse> VerifyAuditAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync("/audit/verify", AmpelumHostContractsJsonContext.Default.AuditVerifyResponse, ct))!;

    /// <summary>Returns the HTTP status of the readiness probe.</summary>
    public async Task<HttpStatusCode> ReadyStatusAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/readyz", ct);
        return response.StatusCode;
    }

    static async Task<AgentServiceException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ErrorResponse? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, ct);
        }
        catch (JsonException)
        {
            // The body was not the service's error shape; the status alone is reported.
        }
        return new AgentServiceException(response.StatusCode, error);
    }
}

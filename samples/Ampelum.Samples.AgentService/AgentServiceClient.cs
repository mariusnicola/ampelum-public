using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Persistence;

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
        return await ReadFramesAsync(request, ct);
    }

    /// <summary>Sends the request and returns every SSE frame to the end of the response.</summary>
    async Task<IReadOnlyList<StreamFrame>> ReadFramesAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);

        var frames = new List<StreamFrame>();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(ct))
            frames.Add(new StreamFrame(item.EventType, JsonDocument.Parse(item.Data).RootElement.Clone()));
        return frames;
    }

    /// <summary>Calls <c>GET /prompts</c>: the modules that have prompt versions.</summary>
    public async Task<PromptIndexResponse> ListPromptModulesAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync("/prompts", AmpelumHostContractsJsonContext.Default.PromptIndexResponse, ct))!;

    /// <summary>Calls <c>GET /prompts/{module}</c>: every prompt version of the module.</summary>
    public async Task<PromptVersionList> ListPromptVersionsAsync(string module, CancellationToken ct = default) =>
        await GetAsync($"/prompts/{module}", AmpelumHostContractsJsonContext.Default.PromptVersionList, ct);

    /// <summary>Calls <c>GET /prompts/{module}/active</c>. The Host answers 404 while nothing has been promoted.</summary>
    public async Task<PromptVersion> GetActivePromptAsync(string module, CancellationToken ct = default) =>
        await GetAsync($"/prompts/{module}/active", AmpelumHostContractsJsonContext.Default.PromptVersion, ct);

    /// <summary>Calls <c>POST /prompts/{module}</c>: stores a new challenger version.</summary>
    public async Task<PromptVersion> CreatePromptVersionAsync(
        string module, CreatePromptVersionRequest body, CancellationToken ct = default) =>
        await PostAsync($"/prompts/{module}", body, AmpelumHostContractsJsonContext.Default.CreatePromptVersionRequest,
            AmpelumHostContractsJsonContext.Default.PromptVersion, ct);

    /// <summary>Calls <c>POST /prompts/{module}/promote</c>: makes a challenger the active version.</summary>
    public async Task<PromoteResponse> PromoteAsync(string module, string versionId, CancellationToken ct = default) =>
        await PostAsync($"/prompts/{module}/promote", new PromoteRequest(versionId),
            AmpelumHostContractsJsonContext.Default.PromoteRequest, AmpelumHostContractsJsonContext.Default.PromoteResponse, ct);

    /// <summary>Calls <c>GET /audit/{traceId}</c>. The Host finds the entry through its index, which only a rebuild fills.</summary>
    public async Task<AuditEntryDto> GetAuditByTraceAsync(string traceId, CancellationToken ct = default) =>
        await GetAsync($"/audit/{traceId}", AmpelumHostContractsJsonContext.Default.AuditEntryDto, ct);

    /// <summary>Calls <c>POST /audit/replay/{traceId}</c>: the recorded outputs, served without calling the model.</summary>
    public async Task<ForwardResponse> ReplayAsync(string traceId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"/audit/replay/{traceId}", content: null, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ForwardResponse, ct))!;
    }

    /// <summary>Calls <c>POST /optimize/{module}</c>. The Host answers 202 with the run's id.</summary>
    public async Task<OptimizeRunHandle> StartOptimizeAsync(
        string module, OptimizeRequest body, CancellationToken ct = default) =>
        await PostAsync($"/optimize/{module}", body, AmpelumHostContractsJsonContext.Default.OptimizeRequest,
            AmpelumHostContractsJsonContext.Default.OptimizeRunHandle, ct);

    /// <summary>Calls <c>GET /optimize/runs/{runId}</c>.</summary>
    public async Task<OptimizeRunStatus> GetOptimizeStatusAsync(string runId, CancellationToken ct = default) =>
        await GetAsync($"/optimize/runs/{runId}",AmpelumHostContractsJsonContext.Default.OptimizeRunStatus, ct);

    /// <summary>
    /// Polls <see cref="GetOptimizeStatusAsync"/> until the run is no longer queued or running.
    /// </summary>
    /// <exception cref="TimeoutException">The run did not finish within <paramref name="limit"/>.</exception>
    public async Task<OptimizeRunStatus> WaitForOptimizeAsync(string runId, TimeSpan limit, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(limit);
        try
        {
            while (true)
            {
                var status = await GetOptimizeStatusAsync(runId, linked.Token);
                if (status.State is not (OptimizeRunState.Queued or OptimizeRunState.Running))
                    return status;
                await Task.Delay(20, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Optimizer run {runId} did not finish within {limit.TotalSeconds:0.###} seconds.");
        }
    }

    /// <summary>
    /// Calls <c>GET /optimize/runs/{runId}/stream</c> and returns the frames to the end of the response. For a run that
    /// already finished the Host rebuilds the frames from the stored run: <c>final</c> then <c>done</c>.
    /// </summary>
    public async Task<IReadOnlyList<StreamFrame>> StreamOptimizeAsync(string runId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/optimize/runs/{runId}/stream");
        return await ReadFramesAsync(request, ct);
    }

    /// <summary>Returns the HTTP status of the liveness probe.</summary>
    public async Task<HttpStatusCode> HealthStatusAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/healthz", ct);
        return response.StatusCode;
    }

    /// <summary>Calls <c>POST /admin/rebuild-index</c>. The route exists only when the Host enables its admin endpoints.</summary>
    public async Task<RebuildSummary> RebuildIndexAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("/admin/rebuild-index", content: null, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RebuildSummary>(JsonSerializerOptions.Web, ct))!;
    }

    /// <summary>Fetches the OpenAPI document the Host publishes.</summary>
    public async Task<JsonElement> GetOpenApiAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/openapi/v1.json", ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).Clone();
    }

    async Task<T> GetAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync(type, ct))!;
    }

    async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, requestType, ct);
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync(responseType, ct))!;
    }

    public async Task<AuditPage> ListAuditAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync("/audit", AmpelumHostContractsJsonContext.Default.AuditPage, ct))!;

    public async Task<AuditVerifyResponse> VerifyAuditAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync("/audit/verify", AmpelumHostContractsJsonContext.Default.AuditVerifyResponse, ct))!;

    /// <summary>Returns the HTTP status of the readiness probe.</summary>
    public async Task<HttpStatusCode> ReadyStatusAsync(CancellationToken ct = default) =>
        (await ReadyAsync(ct)).Status;

    /// <summary>Returns the status and body of one readiness probe.</summary>
    public async Task<(HttpStatusCode Status, string Body)> ReadyAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/readyz", ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Polls the readiness probe until it answers 200 or the attempts run out, and returns the last answer.
    /// A probe is meant to be polled: the Host reports unready while its first SQLite open exceeds the
    /// probe's latency budget, which can happen on a cold start.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> WaitUntilReadyAsync(int attempts, TimeSpan pause, CancellationToken ct = default)
    {
        var last = await ReadyAsync(ct);
        for (var i = 1; i < attempts && last.Status != HttpStatusCode.OK; i++)
        {
            await Task.Delay(pause, ct);
            last = await ReadyAsync(ct);
        }
        return last;
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

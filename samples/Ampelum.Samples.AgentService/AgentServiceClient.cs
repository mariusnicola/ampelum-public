using System.Net;
using System.Net.Http.Json;
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

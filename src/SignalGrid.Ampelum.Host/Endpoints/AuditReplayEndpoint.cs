#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ErrorHandling;
using ErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Endpoints;

/// <summary>
/// Audit replay endpoint: re-execute forward calls from audit entries.
/// Spec §8. Host Plan 4 implements the full replay logic.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal static class AuditReplayEndpoint
{
    public static WebApplication MapAuditReplayEndpoints(this WebApplication app)
    {
        app.MapPost("/audit/replay/{traceId}", ReplayAsync)
            .WithName("ReplayAuditEntry")
            .WithSummary("Replay a forward call from an audit entry.")
            .WithTags("Audit");
        return app;
    }

    static async Task<IResult> ReplayAsync(
        string traceId,
        Replay.IAuditReplayService replayService,
        CancellationToken ct)
    {
        var requestTraceId = TraceIdResolver.Resolve();
        try
        {
            var response = await replayService.ReplayAsync(traceId, ct);
            return Results.Ok(response);
        }
        catch (KeyNotFoundException ex) when (ex.Message.StartsWith("trace_not_found:"))
        {
            return ApiErrors.NotFound(
                ErrorCodes.TraceNotFound,
                $"Audit entry '{traceId}' not found.",
                requestTraceId);
        }
        catch (KeyNotFoundException ex) when (ex.Message.StartsWith("module_not_found:"))
        {
            var moduleName = ex.Message.Substring("module_not_found:".Length);
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' is no longer registered.",
                requestTraceId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Replay hash mismatch"))
        {
            return ApiErrors.Unprocessable(
                ErrorCodes.SignatureValidationFailed,
                ex.Message,
                requestTraceId);
        }
        catch (Replay.ReplayLmCalledException ex)
        {
            return ApiErrors.ServiceUnavailable(
                ErrorCodes.ReplayLmCalled,
                ex.Message,
                requestTraceId);
        }
    }
}

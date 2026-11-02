#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ErrorHandling;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Sse;
using SignalGrid.Ampelum.Host.Tracing;
using ErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;

namespace SignalGrid.Ampelum.Host.Endpoints;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class OptimizeEndpoint
{
    public static WebApplication MapOptimizeEndpoints(this WebApplication app)
    {
        app.MapPost("/optimize/{moduleName}", StartOptimizeAsync)
            .WithName("StartOptimize")
            .WithSummary("Start an optimizer run.")
            .WithTags("Optimize");

        app.MapGet("/optimize/runs/{runId}", GetOptimizeStatusAsync)
            .WithName("GetOptimizeStatus")
            .WithSummary("Get the status of an optimizer run.")
            .WithTags("Optimize");

        app.MapGet("/optimize/runs/{runId}/stream", StreamOptimizeAsync)
            .WithName("StreamOptimize")
            .WithSummary("Stream progress events for an optimizer run.")
            .WithTags("Optimize");

        return app;
    }

    static async Task<IResult> StartOptimizeAsync(
        string moduleName,
        HttpRequest request,
        IModuleRegistry registry,
        IOptimizerRunStore runStore,
        IOptimizerRunner runner,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve();

        if (!registry.TryGet(moduleName, out _))
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);

        OptimizeRequest? req = null;
        try
        {
            req = await request.HttpContext.Request.ReadFromJsonAsync<OptimizeRequest>(
                AmpelumHostJsonContext.Default.OptimizeRequest,
                ct);
        }
        catch (JsonException ex)
        {
            return ApiErrors.BadRequest(
                ErrorCodes.BadRequest,
                $"Invalid request JSON: {ex.Message}",
                traceId);
        }
        catch (Exception ex)
        {
            return ApiErrors.BadRequest(
                ErrorCodes.BadRequest,
                $"Failed to deserialize request: {ex.Message}",
                traceId);
        }

        if (req is null)
            return ApiErrors.BadRequest(
                ErrorCodes.BadRequest,
                "Request body is required.",
                traceId);

        if (!OptimizerTypes.All.Contains(req.OptimizerType))
            return ApiErrors.Unprocessable(
                ErrorCodes.SignatureValidationFailed,
                $"Unknown optimizer type '{req.OptimizerType}'.",
                traceId);

        var runId = "run-" + Guid.NewGuid().ToString("N")[..12];
        var row = new OptimizerRunRow(
            runId,
            moduleName,
            req.OptimizerType,
            "queued",
            DateTimeOffset.UtcNow,
            null,
            0,
            null,
            null,
            null);

        await runStore.InsertAsync(row, ct);
        await runner.EnqueueAsync(runId, req, ct);

        var handle = new OptimizeRunHandle(runId, moduleName);
        request.HttpContext.Response.StatusCode = StatusCodes.Status202Accepted;
        request.HttpContext.Response.Headers.Location = $"/optimize/runs/{runId}";
        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
        return Results.Json(handle, AmpelumHostJsonContext.Default.OptimizeRunHandle);
    }

    static async Task<IResult> GetOptimizeStatusAsync(
        string runId,
        IOptimizerRunStore runStore,
        HttpRequest request,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve();

        var row = await runStore.TryGetAsync(runId, ct);
        if (row is null)
            return ApiErrors.NotFound(
                ErrorCodes.RunNotFound,
                $"Optimizer run '{runId}' not found.",
                traceId);

        var status = new OptimizeRunStatus(
            row.RunId,
            row.ModuleName,
            row.OptimizerType,
            row.State,
            row.StartedAt,
            row.CompletedAt,
            row.TrialsCompleted,
            row.TrialsTotal,
            row.ResultVersionId,
            row.ErrorMessage);

        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
        return Results.Json(status, AmpelumHostJsonContext.Default.OptimizeRunStatus);
    }

    static async Task StreamOptimizeAsync(
        string runId,
        IOptimizerRunStore runStore,
        IOptimizerProgressBus progressBus,
        HttpRequest request,
        IOptions<AmpelumHostOptions> options,
        ILogger<object> logger,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve();
        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);

        var row = await runStore.TryGetAsync(runId, ct);
        if (row is null)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            var errorResp = new ErrorResponse(traceId, ErrorCodes.RunNotFound, $"Run '{runId}' not found.", null);
            await request.HttpContext.Response.WriteAsJsonAsync(errorResp, AmpelumHostJsonContext.Default.ErrorResponse, cancellationToken: ct);
            return;
        }

        var keepAlive = TimeSpan.FromSeconds(options.Value.Sse.KeepAliveSeconds);
        var sseWriter = new SseWriter(request.HttpContext.Response, logger, keepAlive);
        await sseWriter.StartAsync(ct);

        try
        {
            // Default NN: if run is already in a terminal state, reconstruct events from the store row.
            if (row.State is OptimizeRunState.Succeeded or OptimizeRunState.Failed or OptimizeRunState.Cancelled)
            {
                if (row.State == OptimizeRunState.Succeeded && row.ResultVersionId is not null)
                {
                    var pv = new PromptVersion(row.ResultVersionId, row.ModuleName,
                        row.CompletedAt ?? row.StartedAt, PromptStatus.Challenger,
                        new Dictionary<string, IReadOnlyList<DemoExample>>(),
                        new Dictionary<string, string>(), null, null);
                    var finalJson = JsonSerializer.Serialize(pv, AmpelumHostJsonContext.Default.PromptVersion);
                    await sseWriter.WriteEventAsync(SseEventNames.Final, finalJson, 1, ct);
                }
                else
                {
                    var reason = row.State == OptimizeRunState.Cancelled ? "cancelled" : (row.ErrorMessage ?? "failed");
                    var errJson = JsonSerializer.Serialize(new { reason }, JsonSerializerOptions.Default);
                    await sseWriter.WriteEventAsync(SseEventNames.Error, errJson, 1, ct);
                }
                await sseWriter.WriteEventAsync(SseEventNames.Done, "{}", 2, ct);
                return;
            }

            var stream = progressBus.Subscribe(runId, ct);
            await foreach (var envelope in stream.WithCancellation(ct))
            {
                await sseWriter.WriteEventAsync(envelope.EventName, envelope.JsonPayload, envelope.Id, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or context cancelled
        }
        finally
        {
            await sseWriter.DisposeAsync();
        }
    }
}

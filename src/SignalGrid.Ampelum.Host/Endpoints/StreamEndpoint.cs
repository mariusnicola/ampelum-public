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
using SignalGrid.Ampelum.Host.Sse;
using SignalGrid.Ampelum.Host.Tracing;
using SignalGrid.Ampelum;
using ErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;

namespace SignalGrid.Ampelum.Host.Endpoints;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class StreamEndpoint
{
    public static WebApplication MapStreamEndpoint(this WebApplication app)
    {
        app.MapPost("/modules/{moduleName}/stream", HandleAsync)
            .WithName("StreamModule")
            .WithSummary("Stream a module's output incrementally.")
            .WithTags("Modules");
        return app;
    }

    static async Task HandleAsync(
        string moduleName,
        HttpRequest request,
        IModuleRegistry registry,
        IOptions<AmpelumHostOptions> options,
        ILogger<object> logger,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve(request.Headers.TryGetValue("X-Ampelum-Parent-Trace-Id", out var values)
            ? values.FirstOrDefault()
            : null);

        if (!registry.TryGet(moduleName, out var slot))
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            var error = ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.ModuleNotFound, $"Module '{moduleName}' not found.", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }

        if (!slot.SupportsStreaming)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.SignatureValidationFailed, "Module does not support streaming.", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }

        ForwardRequest? req = null;
        try
        {
            req = await request.HttpContext.Request.ReadFromJsonAsync<ForwardRequest>(
                AmpelumHostJsonContext.Default.ForwardRequest,
                ct);
        }
        catch (JsonException ex)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.BadRequest, $"Invalid request JSON: {ex.Message}", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }
        catch (Exception ex)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.BadRequest, $"Failed to deserialize request: {ex.Message}", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }

        if (req is null)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.BadRequest, "Request body is required.", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }

        IReadOnlyDictionary<string, object?> unboxedInputs;
        try
        {
            unboxedInputs = JsonElementUnboxer.Unbox(req.Inputs);
        }
        catch (Exception ex)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.BadRequest, $"Failed to unbox inputs: {ex.Message}", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }

        var adapterType = slot.Current.GetType();
        var sigType = adapterType.GetGenericArguments()[0];

        object? typedSig = null;
        try
        {
            var projectMethod = typeof(SignaturePropertyProjector)
                .GetMethod(nameof(SignaturePropertyProjector.ProjectTo))!
                .MakeGenericMethod(sigType);
            typedSig = projectMethod.Invoke(null, new object[] { unboxedInputs })!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException argEx)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.SignatureValidationFailed, argEx.Message, null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }
        catch (ArgumentException ex)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.SignatureValidationFailed, ex.Message, null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }
        catch (Exception ex)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.SignatureValidationFailed, $"Failed to project inputs to signature: {ex.Message}", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }

        var sseWriter = new SseWriter(
            request.HttpContext.Response,
            logger,
            TimeSpan.FromSeconds(options.Value.Sse.KeepAliveSeconds));

        await sseWriter.StartAsync(ct);

        try
        {
            dynamic predict = ((dynamic)slot.Current).Inner;
            IAsyncEnumerable<dynamic> stream = predict.StreamAsync((dynamic)typedSig, null, ct);

            int eventId = 0;
            await foreach (var chunk in stream.WithCancellation(ct))
            {
                var outputs = (IReadOnlyDictionary<string, object?>)chunk.Trace.Outputs;
                var boxedOutputs = JsonElementUnboxer.Box(outputs);

                var partialPayload = new SsePartialPayload(traceId, "output", JsonSerializer.Serialize(boxedOutputs));
                var jsonPayload = JsonSerializer.Serialize(partialPayload, AmpelumHostJsonContext.Default.SsePartialPayload);
                await sseWriter.WriteEventAsync(SseEvents.Partial, jsonPayload, eventId++, ct);
            }

            // Final event: emit full outputs
            var lastChunk = stream as dynamic;
            var finalOutputs = (IReadOnlyDictionary<string, object?>)lastChunk.Trace.Outputs;
            var boxedFinalOutputs = JsonElementUnboxer.Box(finalOutputs);

            var usage = lastChunk.Trace.Usage ?? new Microsoft.Extensions.AI.UsageDetails();
            var modelId = "unknown";
            if (lastChunk.Trace.Metadata.TryGetValue("model_id", out object? modelIdObj))
            {
                if (modelIdObj is string modelIdStr)
                    modelId = modelIdStr;
            }

            var finalPayload = new SseFinalPayload(
                traceId,
                boxedFinalOutputs,
                new UsageInfo(
                    PromptTokens: usage.InputTokenCount ?? 0,
                    CompletionTokens: usage.OutputTokenCount ?? 0,
                    CostCents: 0m,
                    DurationMs: lastChunk.Trace.Duration.TotalMilliseconds),
                modelId,
                null);

            var finalJson = JsonSerializer.Serialize(finalPayload, AmpelumHostJsonContext.Default.SseFinalPayload);
            await sseWriter.WriteEventAsync(SseEvents.Final, finalJson, eventId++, ct);

            // Done event
            var donePayload = new SseDonePayload(traceId);
            var doneJson = JsonSerializer.Serialize(donePayload, AmpelumHostJsonContext.Default.SseDonePayload);
            await sseWriter.WriteEventAsync(SseEvents.Done, doneJson, eventId++, ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            try
            {
                var errorPayload = new SseErrorPayload(
                    traceId,
                    ErrorCodes.BadRequest,
                    $"Stream failed: {ex.Message}");
                var errorJson = JsonSerializer.Serialize(errorPayload, AmpelumHostJsonContext.Default.SseErrorPayload);
                await sseWriter.WriteEventAsync(SseEvents.Error, errorJson, null, ct);

                var donePayload = new SseDonePayload(traceId);
                var doneJson = JsonSerializer.Serialize(donePayload, AmpelumHostJsonContext.Default.SseDonePayload);
                await sseWriter.WriteEventAsync(SseEvents.Done, doneJson, null, ct);
            }
            catch
            {
                // Best effort; client may have disconnected
            }
        }
        finally
        {
            await sseWriter.DisposeAsync();
        }
    }
}

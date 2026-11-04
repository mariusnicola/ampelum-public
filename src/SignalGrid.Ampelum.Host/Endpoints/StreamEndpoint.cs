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
        IPromptVersionStore prompts,
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

        // Same version rules as forward: the one the caller names, else the active one. Both failures are answered
        // as plain JSON, before the event stream starts.
        var (version, versionMissing) = await PromptVersionSelector.FindAsync(prompts, moduleName, req.PromptVersionId, ct);
        if (versionMissing)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await request.HttpContext.Response.WriteAsJsonAsync(
                new ErrorResponse(traceId, ErrorCodes.VersionNotFound, $"Version '{req.PromptVersionId}' not found.", null),
                AmpelumHostJsonContext.Default.ErrorResponse,
                cancellationToken: ct);
            return;
        }

        VersionContent? content = null;
        if (version is not null)
        {
            try
            {
                content = await PromptVersionSelector.LoadAsync(version, ct);
            }
            catch (Exception ex)
            {
                request.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await request.HttpContext.Response.WriteAsJsonAsync(
                    new ErrorResponse(traceId, ErrorCodes.BadRequest, $"Stream failed: {ex.Message}", null),
                    AmpelumHostJsonContext.Default.ErrorResponse,
                    cancellationToken: ct);
                return;
            }
        }

        var sseWriter = new SseWriter(
            request.HttpContext.Response,
            logger,
            TimeSpan.FromSeconds(options.Value.Sse.KeepAliveSeconds));

        await sseWriter.StartAsync(ct);

        try
        {
            var run = typeof(StreamEndpoint)
                .GetMethod(nameof(RunStreamAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(sigType);
            await (Task)run.Invoke(null, new object?[] { slot.Current, typedSig!, sseWriter, traceId, content, version?.VersionId, ct })!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected. Any other cancellation, such as a model timeout, is a failure and reaches the error frame below.
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

    // The signature type is only known at runtime, so the typed loop lives in a generic method
    // reached through one reflection call instead of dynamic dispatch.
    static async Task RunStreamAsync<TSig>(
        PredictComposedAdapter<TSig> adapter,
        TSig inputs,
        SseWriter sseWriter,
        string traceId,
        VersionContent? content,
        string? versionId,
        CancellationToken ct) where TSig : ISignature
    {
        var predict = content is null ? adapter.Inner : PromptVersionSelector.Apply(adapter.Inner, content);
        int eventId = 0;
        var outputNames = SignatureMetadataCache.For<TSig>().Outputs.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        TraceRecord? finalTrace = null;
        await foreach (var update in predict.StreamAsync(inputs, null, ct).WithCancellation(ct))
        {
            if (update.IsFinal)
            {
                finalTrace = update.Trace;
                continue;
            }

            // The adapter also reports markers that are not outputs, such as the terminator, and an empty name before the first marker.
            if (!outputNames.Contains(update.FieldName))
                continue;

            var partialPayload = new SsePartialPayload(traceId, update.FieldName, update.PartialText);
            var partialJson = JsonSerializer.Serialize(partialPayload, AmpelumHostJsonContext.Default.SsePartialPayload);
            await sseWriter.WriteEventAsync(SseEvents.Partial, partialJson, eventId++, ct);
        }

        // StreamAsync throws on failure, so ending without a final update would be a bug.
        if (finalTrace is null)
            throw new InvalidOperationException("The stream ended without a final update.");

        var usage = finalTrace.Usage;
        var modelId = finalTrace.Metadata.TryGetValue("model_id", out var modelIdObj) && modelIdObj is string modelIdStr
            ? modelIdStr
            : "unknown";

        var finalPayload = new SseFinalPayload(
            traceId,
            JsonElementUnboxer.Box(finalTrace.Outputs),
            new UsageInfo(
                PromptTokens: usage?.InputTokenCount ?? 0,
                CompletionTokens: usage?.OutputTokenCount ?? 0,
                CostCents: 0m,
                DurationMs: finalTrace.Duration.TotalMilliseconds),
            modelId,
            versionId);
        var finalJson = JsonSerializer.Serialize(finalPayload, AmpelumHostJsonContext.Default.SseFinalPayload);
        await sseWriter.WriteEventAsync(SseEvents.Final, finalJson, eventId++, ct);

        var doneJson = JsonSerializer.Serialize(new SseDonePayload(traceId), AmpelumHostJsonContext.Default.SseDonePayload);
        await sseWriter.WriteEventAsync(SseEvents.Done, doneJson, eventId, ct);
    }
}

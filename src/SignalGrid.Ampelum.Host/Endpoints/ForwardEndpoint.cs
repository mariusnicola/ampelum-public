#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SignalGrid.Ampelum;
using Microsoft.AspNetCore.Http;
using SignalGrid.Ampelum.Host.Contracts;
using ErrorResponse = SignalGrid.Ampelum.Host.Contracts.ErrorResponse;
using SignalGrid.Ampelum.Host.ErrorHandling;
using ErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;
using SignalGrid.Ampelum.Host.Internal;
using AmpelumHostContractsJsonContext = SignalGrid.Ampelum.Host.Contracts.AmpelumHostContractsJsonContext;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Endpoints;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class ForwardEndpoint
{
    public static WebApplication MapForwardEndpoint(this WebApplication app)
    {
        app.MapPost("/modules/{moduleName}/forward", HandleAsync)
            .WithName("ForwardModule")
            .WithSummary("Synchronously invoke a registered module.")
            .WithTags("Modules");
        return app;
    }

    static async Task<IResult> HandleAsync(
        string moduleName,
        HttpRequest request,
        IModuleRegistry registry,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve(request.Headers.TryGetValue("X-Ampelum-Parent-Trace-Id", out var values)
            ? values.FirstOrDefault()
            : null);

        if (!registry.TryGet(moduleName, out var slot))
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);

        ForwardRequest? req = null;
        try
        {
            req = await request.HttpContext.Request.ReadFromJsonAsync<ForwardRequest>(
                AmpelumHostJsonContext.Default.ForwardRequest,
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

        IReadOnlyDictionary<string, object?> unboxedInputs;
        try
        {
            unboxedInputs = JsonElementUnboxer.Unbox(req.Inputs);
        }
        catch (Exception ex)
        {
            return ApiErrors.BadRequest(
                ErrorCodes.BadRequest,
                $"Failed to unbox inputs: {ex.Message}",
                traceId);
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
            return ApiErrors.Unprocessable(
                ErrorCodes.SignatureValidationFailed,
                argEx.Message,
                traceId);
        }
        catch (ArgumentException ex)
        {
            return ApiErrors.Unprocessable(
                ErrorCodes.SignatureValidationFailed,
                ex.Message,
                traceId);
        }
        catch (Exception ex)
        {
            return ApiErrors.Unprocessable(
                ErrorCodes.SignatureValidationFailed,
                $"Failed to project inputs to signature: {ex.Message}",
                traceId);
        }

        try
        {
            dynamic predict = ((dynamic)slot.Current).Inner;
            var prediction = await predict.ForwardAsync((dynamic)typedSig, null, ct);

            var outputs = (IReadOnlyDictionary<string, object?>)prediction.Trace.Outputs;
            var boxedOutputs = JsonElementUnboxer.Box(outputs);

            var usage = prediction.Trace.Usage ?? new Microsoft.Extensions.AI.UsageDetails();
            var modelId = "unknown";
            if (prediction.Trace.Metadata.TryGetValue("model_id", out object? modelIdObj))
            {
                if (modelIdObj is string modelIdStr)
                    modelId = modelIdStr;
            }

            var response = new ForwardResponse(
                TraceId: traceId,
                Outputs: boxedOutputs,
                Usage: new UsageInfo(
                    PromptTokens: usage.InputTokenCount ?? 0,
                    CompletionTokens: usage.OutputTokenCount ?? 0,
                    CostCents: 0m,
                    DurationMs: prediction.Trace.Duration.TotalMilliseconds),
                ModelId: modelId,
                ModuleName: moduleName,
                PromptVersionId: req.PromptVersionId);

            request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
            return Results.Json(response, AmpelumHostContractsJsonContext.Default.ForwardResponse);
        }
        catch (Exception ex)
        {
            return ApiErrors.BadRequest(
                ErrorCodes.BadRequest,
                $"Forward call failed: {ex.Message}",
                traceId);
        }
    }
}

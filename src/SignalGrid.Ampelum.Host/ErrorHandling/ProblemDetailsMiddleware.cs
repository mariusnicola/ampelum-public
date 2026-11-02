using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.ErrorHandling;

/// <summary>
/// Middleware that catches unhandled exceptions and maps them to standardized
/// <see cref="ErrorResponse"/> bodies with appropriate HTTP status codes.
/// Also ensures every response includes an X-Ampelum-Trace-Id header.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed class ProblemDetailsMiddleware(RequestDelegate next, ILogger<ProblemDetailsMiddleware> log)
{
    const string TraceIdHeaderName = "X-Ampelum-Trace-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var traceId = TraceIdResolver.Resolve();

        ctx.Response.OnStarting(() =>
        {
            if (!ctx.Response.Headers.ContainsKey(TraceIdHeaderName))
                ctx.Response.Headers.Append(TraceIdHeaderName, traceId);
            return Task.CompletedTask;
        });

        try
        {
            await next(ctx);
        }
        catch (Exception ex)
        {
            await MapExceptionAsync(ctx, ex, traceId);
        }
    }

    async Task MapExceptionAsync(HttpContext ctx, Exception ex, string traceId)
    {
        // If response has already started, we cannot modify headers/status.
        if (ctx.Response.HasStarted)
        {
            log.LogError(ex, "Exception occurred after response started. TraceId: {TraceId}", traceId);
            return;
        }

        var (statusCode, errorCode, message) = MapException(ex);

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = statusCode;
        ctx.Response.Headers[TraceIdHeaderName] = traceId;

        var body = new ErrorResponse(traceId, errorCode, message, null);
        var json = JsonSerializer.SerializeToUtf8Bytes(body, AmpelumHostJsonContext.Default.ErrorResponse);
        await ctx.Response.Body.WriteAsync(json);

        if (statusCode >= 500)
            log.LogError(ex, "Server error ({StatusCode}). TraceId: {TraceId}", statusCode, traceId);
    }

    (int statusCode, string code, string message) MapException(Exception ex)
    {
        return ex switch
        {
            OperationCanceledException => (499, "client_closed_request", "Request cancelled."),
            JsonException => (400, ErrorCodes.BadRequest, "Malformed JSON."),
            BudgetExceededException => (429, ErrorCodes.BudgetExceeded, "Budget exceeded."),
            AdapterParseException ape => (422, ErrorCodes.SignatureValidationFailed, ape.Hint),
            ArgumentException => (422, ErrorCodes.SignatureValidationFailed, "Invalid argument."),
            PromoteConflictException pce => (409, ErrorCodes.PromoteConflict, pce.Message),
            KeyNotFoundException kne => (404, GetKeyNotFoundCode(kne), kne.Message),
            _ => (500, ErrorCodes.InternalError, "Internal server error."),
        };
    }

    static string GetKeyNotFoundCode(KeyNotFoundException ex)
    {
        // If ex.Data["host.error_code"] is set, use that; otherwise use the default not_found code.
        if (ex.Data.Contains("host.error_code"))
            return ex.Data["host.error_code"]?.ToString() ?? ErrorCodes.NotFound;
        return ErrorCodes.NotFound;
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Internal;

namespace SignalGrid.Ampelum.Host.ErrorHandling;

/// <summary>
/// Helper methods for creating standardized <see cref="ErrorResponse"/> results
/// for different HTTP status codes.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal static class ApiErrors
{
    /// <summary>404 Not Found.</summary>
    public static IResult NotFound(
        string code,
        string message,
        string traceId,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => Results.Json(
            new ErrorResponse(traceId, code, message, details),
            jsonTypeInfo: AmpelumHostJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>400 Bad Request.</summary>
    public static IResult BadRequest(
        string code,
        string message,
        string traceId,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => Results.Json(
            new ErrorResponse(traceId, code, message, details),
            jsonTypeInfo: AmpelumHostJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>409 Conflict.</summary>
    public static IResult Conflict(
        string code,
        string message,
        string traceId,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => Results.Json(
            new ErrorResponse(traceId, code, message, details),
            jsonTypeInfo: AmpelumHostJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>422 Unprocessable Entity.</summary>
    public static IResult Unprocessable(
        string code,
        string message,
        string traceId,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => Results.Json(
            new ErrorResponse(traceId, code, message, details),
            jsonTypeInfo: AmpelumHostJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>429 Too Many Requests.</summary>
    public static IResult TooManyRequests(
        string code,
        string message,
        string traceId,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => Results.Json(
            new ErrorResponse(traceId, code, message, details),
            jsonTypeInfo: AmpelumHostJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>503 Service Unavailable.</summary>
    public static IResult ServiceUnavailable(
        string code,
        string message,
        string traceId,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => Results.Json(
            new ErrorResponse(traceId, code, message, details),
            jsonTypeInfo: AmpelumHostJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

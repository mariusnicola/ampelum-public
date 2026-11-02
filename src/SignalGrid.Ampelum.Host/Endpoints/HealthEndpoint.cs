#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using SignalGrid.Ampelum.Host.Internal;
using AmpelumHostJsonContext = SignalGrid.Ampelum.Host.Internal.AmpelumHostJsonContext;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Endpoints;

/// <summary>
/// Health and readiness endpoints for Kubernetes probes.
/// /healthz: liveness (always 200 if process is running).
/// /readyz: readiness (200 if ready to serve, 503 if split-brain or no modules).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal static class HealthEndpoint
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok())
            .WithName("Healthz")
            .WithSummary("Process liveness probe.")
            .WithTags("Health");
        app.MapGet("/readyz", ReadyAsync)
            .WithName("Readyz")
            .WithSummary("Readiness probe.")
            .WithTags("Health");
        return app;
    }

    static async Task<IResult> ReadyAsync(
        IModuleRegistry registry,
        IReadinessState readiness,
        IHostSqliteConnectionFactory sqliteFactory,
        CancellationToken ct)
    {
        // Check readiness state
        if (!readiness.IsReady)
            return Results.Json(
                new ReadyResponse(false, readiness.UnreadyReason),
                AmpelumHostJsonContext.Default.ReadyResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);

        // Check module registry not empty
        if (registry.Count == 0)
            return Results.Json(
                new ReadyResponse(false, "No modules registered"),
                AmpelumHostJsonContext.Default.ReadyResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);

        // Check SQLite connectivity within 50ms
        try
        {
            using var conn = sqliteFactory.OpenWithLatencyBudget(TimeSpan.FromMilliseconds(50));
        }
        catch
        {
            return Results.Json(
                new ReadyResponse(false, "SQLite unavailable"),
                AmpelumHostJsonContext.Default.ReadyResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(
            new ReadyResponse(true, null),
            AmpelumHostJsonContext.Default.ReadyResponse);
    }
}

/// <summary>Response body for readiness probe.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed record ReadyResponse(
    [property: JsonPropertyName("ok")]
    bool Ok,
    [property: JsonPropertyName("reason")]
    string? Reason);

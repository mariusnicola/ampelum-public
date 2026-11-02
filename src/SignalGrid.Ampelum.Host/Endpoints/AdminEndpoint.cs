#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Endpoints;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class AdminEndpoint
{
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<AmpelumHostOptions>>();
        if (!options.Value.AdminEndpointsEnabled)
            return app;

        app.MapPost("/admin/rebuild-index", RebuildIndexAsync)
            .WithName("AdminRebuild")
            .WithSummary("Rebuild all SQLite read-side indexes.")
            .WithTags("Admin");

        return app;
    }

    static async Task<IResult> RebuildIndexAsync(
        IIndexRebuilder rebuilder,
        CancellationToken ct)
    {
        var summary = await rebuilder.RebuildAsync(ct);
        return Results.Json(summary, AmpelumHostJsonContext.Default.RebuildSummary);
    }
}

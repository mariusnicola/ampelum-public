#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;

namespace SignalGrid.Ampelum.Host.Endpoints;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class OpenApiEndpoint
{
    public static WebApplication MapOpenApiEndpoint(this WebApplication app)
    {
        app.MapOpenApi();
        return app;
    }
}

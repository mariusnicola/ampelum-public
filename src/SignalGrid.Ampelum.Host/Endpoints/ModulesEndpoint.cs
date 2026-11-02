#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ErrorHandling;
using ErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;
using SignalGrid.Ampelum.Host.Internal;
using AmpelumHostContractsJsonContext = SignalGrid.Ampelum.Host.Contracts.AmpelumHostContractsJsonContext;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Endpoints;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class ModulesEndpoint
{
    public static WebApplication MapModulesEndpoints(this WebApplication app)
    {
        app.MapGet("/modules", ListModulesAsync)
            .WithName("ListModules")
            .WithSummary("List all registered modules.")
            .WithTags("Modules");
        app.MapGet("/modules/{moduleName}", GetModuleAsync)
            .WithName("GetModule")
            .WithSummary("Get metadata for a specific module.")
            .WithTags("Modules");
        return app;
    }

    static IResult ListModulesAsync(IModuleRegistry registry)
    {
        var descriptors = registry.Names.Select(n => registry.Get(n).Descriptor).ToList();
        var response = new ModuleListResponse(descriptors);
        return Results.Json(response, AmpelumHostContractsJsonContext.Default.ModuleListResponse);
    }

    static IResult GetModuleAsync(
        string moduleName,
        IModuleRegistry registry,
        HttpRequest request)
    {
        var traceId = TraceIdResolver.Resolve();

        if (!registry.TryGet(moduleName, out var slot))
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);

        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
        return Results.Json(slot.Descriptor, AmpelumHostContractsJsonContext.Default.ModuleDescriptor);
    }
}

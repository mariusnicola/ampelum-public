#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ErrorHandling;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tracing;
using ErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;

namespace SignalGrid.Ampelum.Host.Endpoints;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class PromptsEndpoint
{
    public static WebApplication MapPromptsEndpoints(this WebApplication app)
    {
        app.MapGet("/prompts", ListPromptsAsync)
            .WithName("ListPrompts")
            .WithSummary("List all modules with prompt versions.")
            .WithTags("Prompts");

        app.MapGet("/prompts/{moduleName}", GetPromptsForModuleAsync)
            .WithName("GetPrompts")
            .WithSummary("List all prompt versions for a module.")
            .WithTags("Prompts");

        app.MapGet("/prompts/{moduleName}/active", GetActivePromptAsync)
            .WithName("GetActivePrompt")
            .WithSummary("Get the active prompt version for a module.")
            .WithTags("Prompts");

        app.MapPost("/prompts/{moduleName}", CreatePromptVersionAsync)
            .WithName("CreatePromptVersion")
            .WithSummary("Create a new challenger prompt version.")
            .WithTags("Prompts");

        app.MapPost("/prompts/{moduleName}/promote", PromotePromptAsync)
            .WithName("PromotePrompt")
            .WithSummary("Promote a challenger prompt to active.")
            .WithTags("Prompts");

        return app;
    }

    static async Task<IResult> ListPromptsAsync(
        IPromptVersionStore store,
        CancellationToken ct)
    {
        var moduleNames = await store.ListModulesAsync(ct);
        var response = new PromptIndexResponse(moduleNames);
        return Results.Json(response, AmpelumHostJsonContext.Default.PromptIndexResponse);
    }

    static async Task<IResult> GetPromptsForModuleAsync(
        string moduleName,
        IModuleRegistry registry,
        IPromptVersionStore store,
        HttpRequest request,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve();

        if (!registry.TryGet(moduleName, out _))
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);

        var rows = await store.ListByModuleAsync(moduleName, ct);
        var versions = rows.Select(r => RowToVersion(r)).ToList();
        var response = new PromptVersionList(moduleName, versions);

        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
        return Results.Json(response, AmpelumHostJsonContext.Default.PromptVersionList);
    }

    static async Task<IResult> GetActivePromptAsync(
        string moduleName,
        IModuleRegistry registry,
        IPromptVersionStore store,
        HttpRequest request,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve();

        if (!registry.TryGet(moduleName, out _))
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);

        var row = await store.GetActiveAsync(moduleName, ct);
        if (row is null)
            return ApiErrors.NotFound(
                ErrorCodes.NotFound,
                $"No active prompt version for module '{moduleName}'.",
                traceId);

        var version = RowToVersion(row);
        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
        return Results.Json(version, AmpelumHostJsonContext.Default.PromptVersion);
    }

    static async Task<IResult> CreatePromptVersionAsync(
        string moduleName,
        HttpRequest request,
        IModuleRegistry registry,
        IPromptVersionStore store,
        IOptions<AmpelumHostOptions> options,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve();

        if (!registry.TryGet(moduleName, out _))
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);

        CreatePromptVersionRequest? req = null;
        try
        {
            req = await request.HttpContext.Request.ReadFromJsonAsync<CreatePromptVersionRequest>(
                AmpelumHostJsonContext.Default.CreatePromptVersionRequest,
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

        var versionId = "v-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6];
        var promptDir = options.Value.Storage.PromptDir;
        var filePath = Path.Combine(promptDir, moduleName, $"{versionId}.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var promptContent = new
            {
                demoAssignments = req.DemoAssignments,
                instructionOverrides = req.InstructionOverrides,
            };
            var json = JsonSerializer.Serialize(promptContent, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(filePath, json, ct);
        }
        catch (Exception ex)
        {
            return ApiErrors.BadRequest(
                ErrorCodes.BadRequest,
                $"Failed to write prompt file: {ex.Message}",
                traceId);
        }

        var row = new PromptVersionRow(
            versionId,
            moduleName,
            PromptStatus.Challenger,
            DateTimeOffset.UtcNow,
            req.ParentVersionId,
            filePath,
            req.Note);

        await store.InsertChallengerAsync(row, ct);

        var version = RowToVersion(row);
        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
        return Results.Json(version, AmpelumHostJsonContext.Default.PromptVersion);
    }

    static async Task<IResult> PromotePromptAsync(
        string moduleName,
        HttpRequest request,
        IModuleRegistry registry,
        IPromptVersionStore store,
        CancellationToken ct)
    {
        var traceId = TraceIdResolver.Resolve();

        if (!registry.TryGet(moduleName, out _))
            return ApiErrors.NotFound(
                ErrorCodes.ModuleNotFound,
                $"Module '{moduleName}' not found.",
                traceId);

        PromoteRequest? req = null;
        try
        {
            req = await request.HttpContext.Request.ReadFromJsonAsync<PromoteRequest>(
                AmpelumHostJsonContext.Default.PromoteRequest,
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

        var versionRow = await store.TryGetByIdAsync(req.VersionId, ct);
        if (versionRow is null || versionRow.ModuleName != moduleName)
            return ApiErrors.NotFound(
                ErrorCodes.VersionNotFound,
                $"Version '{req.VersionId}' not found.",
                traceId);

        if (versionRow.Status != PromptStatus.Challenger)
            return ApiErrors.Conflict(
                ErrorCodes.PromoteConflict,
                $"Version '{req.VersionId}' is not a challenger (status: {versionRow.Status}).",
                traceId);

        PromoteResult result;
        try
        {
            result = await store.PromoteAtomicallyAsync(moduleName, req.VersionId, ct);
        }
        catch (PromoteConflictException ex)
        {
            return ApiErrors.Conflict(
                ErrorCodes.PromoteConflict,
                ex.Message,
                traceId);
        }

        var response = new PromoteResponse(moduleName, result.NewActiveVersionId, result.PreviousActiveVersionId);
        request.HttpContext.Response.Headers.Append("X-Ampelum-Trace-Id", traceId);
        return Results.Json(response, AmpelumHostJsonContext.Default.PromoteResponse);
    }

    static PromptVersion RowToVersion(PromptVersionRow row)
    {
        var demoAssignments = new Dictionary<string, IReadOnlyList<DemoExample>>();
        var instructionOverrides = new Dictionary<string, string>();

        try
        {
            if (File.Exists(row.FilePath))
            {
                var json = File.ReadAllText(row.FilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("demoAssignments", out var demoElem))
                {
                    foreach (var prop in demoElem.EnumerateObject())
                    {
                        var examples = new List<DemoExample>();
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            var example = JsonSerializer.Deserialize<DemoExample>(item, AmpelumHostJsonContext.Default.DemoExample);
                            if (example is not null)
                                examples.Add(example);
                        }
                        demoAssignments[prop.Name] = examples;
                    }
                }

                if (root.TryGetProperty("instructionOverrides", out var instrElem))
                {
                    foreach (var prop in instrElem.EnumerateObject())
                    {
                        if (prop.Value.GetString() is string val)
                            instructionOverrides[prop.Name] = val;
                    }
                }
            }
        }
        catch
        {
            // Ignore file read errors; return empty dicts
        }

        return new PromptVersion(
            row.VersionId,
            row.ModuleName,
            row.CreatedAt,
            row.Status,
            demoAssignments,
            instructionOverrides,
            row.ParentVersionId,
            row.Note);
    }
}

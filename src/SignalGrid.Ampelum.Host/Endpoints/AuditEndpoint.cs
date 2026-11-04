#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Host.Contracts;
using ErrorResponse = SignalGrid.Ampelum.Host.Contracts.ErrorResponse;
using SignalGrid.Ampelum.Host.ErrorHandling;
using ErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.Replay;
using SignalGrid.Ampelum.Host.Tracing;

namespace SignalGrid.Ampelum.Host.Endpoints;

/// <summary>
/// Audit log read endpoints: list entries (paginated), verify chain integrity,
/// and look up entries by trace ID. Spec §6.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal static class AuditEndpoint
{
    public static WebApplication MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/audit", ListAuditAsync)
            .WithName("ListAudit")
            .WithSummary("Paginated audit log read.")
            .WithTags("Audit");
        app.MapGet("/audit/verify", VerifyAuditAsync)
            .WithName("VerifyAudit")
            .WithSummary("Verify hash-chain integrity of the audit log.")
            .WithTags("Audit");
        app.MapGet("/audit/{traceId}", GetAuditByTraceIdAsync)
            .WithName("GetAuditByTraceId")
            .WithSummary("Get a single audit entry by trace ID.")
            .WithTags("Audit");
        return app;
    }

    static async Task<IResult> ListAuditAsync(
        IAuditSink sink,
        int since = 0,
        int limit = 100,
        CancellationToken ct = default)
    {
        // Validate limit
        if (limit < 1 || limit > 1000)
            return ApiErrors.BadRequest(
                ErrorCodes.BadRequest,
                "limit must be between 1 and 1000.",
                TraceIdResolver.Resolve());

        var entries = new List<AuditEntryDto>();
        long lastSequence = -1;
        var count = 0;

        // Enumerate and collect up to limit entries starting from 'since'
        await foreach (var entry in sink.EnumerateAsync(ct))
        {
            if (entry.Sequence < since)
                continue;

            var traceId = ResolveTraceId(entry);
            var dto = new AuditEntryDto(
                Sequence: entry.Sequence,
                Timestamp: entry.Timestamp,
                TraceId: traceId,
                ModuleName: entry.ModuleName,
                EventType: entry.EventType,
                ModelId: entry.ModelId,
                Inputs: JsonElementUnboxer.Box(entry.Inputs),
                Outputs: JsonElementUnboxer.Box(entry.Outputs),
                PromptTokens: entry.PromptTokens,
                CompletionTokens: entry.CompletionTokens,
                EntryHashHex: Convert.ToHexString(entry.EntryHash).ToLowerInvariant(),
                PrevHashHex: Convert.ToHexString(entry.PrevHash).ToLowerInvariant(),
                SchemaVersion: entry.SchemaVersion);

            entries.Add(dto);
            lastSequence = entry.Sequence;
            count++;

            if (count >= limit)
                break;
        }

        // Determine next token
        long? nextSinceSequence = null;
        if (count == limit && lastSequence >= 0)
            nextSinceSequence = lastSequence + 1;

        var page = new AuditPage(entries, nextSinceSequence);
        return Results.Json(page, AmpelumHostJsonContext.Default.AuditPage);
    }

    static async Task<IResult> VerifyAuditAsync(IAuditSink sink, CancellationToken ct = default)
    {
        var result = await AuditChainVerifier.VerifyAsync(sink, ct);
        var response = new AuditVerifyResponse(result.IsValid, result.BreakAtSequence, result.Reason);
        return Results.Json(response, AmpelumHostJsonContext.Default.AuditVerifyResponse);
    }

    static async Task<IResult> GetAuditByTraceIdAsync(
        string traceId,
        IAuditEntryLookup lookup,
        CancellationToken ct = default)
    {
        var traceIdResolved = TraceIdResolver.Resolve();

        // Validate traceId format
        if (traceId.Length != 32 || !IsLowercaseHex(traceId))
            return ApiErrors.Unprocessable(
                ErrorCodes.BadRequest,
                "traceId must be 32 lowercase hex characters.",
                traceIdResolved);

        var entry = await lookup.TryGetByTraceIdAsync(traceId, ct);
        if (entry is null)
            return ApiErrors.NotFound(
                ErrorCodes.TraceNotFound,
                $"Audit entry '{traceId}' not found.",
                traceIdResolved);

        var dto = new AuditEntryDto(
            Sequence: entry.Sequence,
            Timestamp: entry.Timestamp,
            TraceId: ResolveTraceId(entry),
            ModuleName: entry.ModuleName,
            EventType: entry.EventType,
            ModelId: entry.ModelId,
            Inputs: JsonElementUnboxer.Box(entry.Inputs),
            Outputs: JsonElementUnboxer.Box(entry.Outputs),
            PromptTokens: entry.PromptTokens,
            CompletionTokens: entry.CompletionTokens,
            EntryHashHex: Convert.ToHexString(entry.EntryHash).ToLowerInvariant(),
            PrevHashHex: Convert.ToHexString(entry.PrevHash).ToLowerInvariant(),
            SchemaVersion: entry.SchemaVersion);
        return Results.Json(dto, AmpelumHostJsonContext.Default.AuditEntryDto);
    }

    static string ResolveTraceId(AuditEntry entry) => AuditEntryLookup.ResolveTraceId(entry);

    static bool IsLowercaseHex(string s)
    {
        if (s.Length != 32)
            return false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }
}

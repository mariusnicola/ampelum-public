#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum.Host.Replay;

/// <summary>Service that replays forward calls from audit entries (spec §8).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IAuditReplayService
{
    /// <summary>
    /// Replays a forward call from an audit entry by trace ID.
    /// Throws KeyNotFoundException if the trace ID or module is not found.
    /// Throws InvalidOperationException if the module does not support replay.
    /// </summary>
    ValueTask<ForwardResponse> ReplayAsync(string traceId, CancellationToken ct);
}

/// <summary>Implements IAuditReplayService using the typed IReplayableModule seam.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed class AuditReplayService : IAuditReplayService
{
    readonly IAuditEntryLookup _lookup;
    readonly IModuleRegistry _registry;
    readonly AmpelumContext _baseCtx;
    readonly ILogger<AuditReplayService> _log;

    /// <summary>Initializes a new instance of the AuditReplayService class.</summary>
    public AuditReplayService(
        IAuditEntryLookup lookup,
        IModuleRegistry registry,
        AmpelumContext baseCtx,
        ILogger<AuditReplayService> log)
    {
        _lookup = lookup;
        _registry = registry;
        _baseCtx = baseCtx;
        _log = log;
    }

    /// <inheritdoc/>
    public async ValueTask<ForwardResponse> ReplayAsync(string traceId, CancellationToken ct)
    {
        var entry = await _lookup.TryGetByTraceIdAsync(traceId, ct)
            ?? throw new KeyNotFoundException($"trace_not_found:{traceId}");

        if (!_registry.TryGet(entry.ModuleName, out var slot))
            throw new KeyNotFoundException($"module_not_found:{entry.ModuleName}");

        if (slot.Current is not IReplayableModule replayable)
            throw new InvalidOperationException(
                $"Module '{entry.ModuleName}' does not support replay.");

        var expectedHash = AuditChainHasher.ComputeReplay(
            entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);

        var replayHook = new AuditReplayHook(entry, expectedHash);
        var replayCtx = _baseCtx
            .WithoutHook<AuditHook>()
            .WithoutHook<BudgetHook>()
            .WithHook(replayHook);

        var outputsDict = await replayable.ReplayAsync(entry.Inputs, replayCtx, ct);

        return new ForwardResponse(
            TraceId: traceId,
            Outputs: JsonElementUnboxer.Box(outputsDict),
            Usage: new UsageInfo(0, 0, 0m, 0d),
            ModelId: entry.ModelId,
            ModuleName: entry.ModuleName,
            PromptVersionId: null);
    }
}

using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>
/// Writes safety decisions that no model call produced (a refused tool, a blocked reply) into the same
/// audit chain as the model calls. Values must be JSON primitives and must never hold the text that was removed.
/// </summary>
public sealed class SafetyNotes(AuditHook audit, string traceId)
{
    public const string ToolRefused = "tool.refused";
    public const string ToolRan = "tool.ran";
    public const string ModerationUnavailable = "moderation.unavailable";
    public const string ToolSanitized = "tool.sanitized";
    public const string ReplyBlocked = "reply.blocked";
    public const string ReplySanitized = "reply.sanitized";
    public const string InputFlagged = "input.flagged";

    public async ValueTask RecordAsync(string eventType, IReadOnlyDictionary<string, object?> details, CancellationToken ct)
    {
        var ctx = new HookContext
        {
            ModuleName = "SafetyGateway",
            Inputs = new Dictionary<string, object?> { ["event"] = eventType },
            Outputs = details,
        };
        ctx.Set(AuditMetadataKeys.EventType, eventType);
        ctx.Set(AuditMetadataKeys.TraceId, traceId);
        await audit.OnAfterAsync(ctx, ct);
    }

    public ValueTask RecordRefusalAsync(ToolRefusal refusal, CancellationToken ct) =>
        RecordAsync(ToolRefused, new Dictionary<string, object?>
        {
            ["tool"] = refusal.Tool,
            ["layer"] = refusal.Layer,
            ["reason"] = refusal.Reason,
        }, ct);
}

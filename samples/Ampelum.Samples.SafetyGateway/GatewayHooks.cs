using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>Puts the agent instance id into every audit entry as its trace id, so an entry names the run that made it.</summary>
public sealed class IdentityStampHook(AgentIdentity identity) : IHook
{
    // Runs before the answer cache (5) and the audit hook (10), which both end or read the pipeline early.
    public int Order => 1;

    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        ctx.Set(AuditMetadataKeys.TraceId, identity.InstanceId);
        return default;
    }

    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;

    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}

/// <summary>
/// Cuts known key shapes out of what the model answered before the moderation service and the audit hook see it.
/// Only the hook pipeline's copy changes; the caller's copy is cleaned separately by the gateway.
/// </summary>
public sealed class CredentialRedactionHook(OutboundSanitizer sanitizer) : IHook
{
    // Highest order, so it is the first after-hook to run and everything downstream reads the cleaned text.
    public int Order => 400;

    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;

    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        if (ctx.Outputs is null)
            return default;
        // LogOutput is the source that replaces credentials; only known key shapes are matched.
        ctx.Outputs = ctx.Outputs.ToDictionary(
            kv => kv.Key,
            kv => kv.Value is string text ? sanitizer.Sanitize(text, ScanSource.LogOutput).SanitizedContent : kv.Value,
            StringComparer.Ordinal);
        return default;
    }

    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}

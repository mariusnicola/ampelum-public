using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>Hook (Order=200) that consults <see cref="IToolRegistry"/> + <see cref="ToolGatePolicy"/>
/// for every tool the library offers to the model (<c>HookContext.Metadata["tools.offered"]</c>, set by
/// <c>HookedLmCall</c> and <c>StreamingChatSession</c>) and for every tool listed in
/// <c>HookContext.Metadata["tools.requested"]</c> (an
/// <c>IEnumerable&lt;(string Name, string Version)&gt;</c>). Per-call override:
/// <c>HookContext.Metadata["tools.policy"]</c> as a <see cref="ToolGatePolicy"/> wins over the
/// constructor-injected default.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ToolGateHook : IHook
{
    /// <summary>Metadata key listing requested tools as IEnumerable&lt;(Name, Version)&gt;.</summary>
    public const string RequestedKey = "tools.requested";

    /// <summary>Metadata key listing tool names offered to the model as <c>IEnumerable&lt;string&gt;</c>.
    /// <c>HookedLmCall</c> (so ReAct and the other module calls) and <c>StreamingChatSession</c> set it from the
    /// tools they pass to the model. A name is refused when the registry has no version of it, or when the
    /// policy denies any registered version.</summary>
    public const string OfferedKey = "tools.offered";

    /// <summary>Metadata key carrying a per-call ToolGatePolicy override.</summary>
    public const string PolicyOverrideKey = "tools.policy";

    readonly IToolRegistry _registry;
    readonly ToolGatePolicy _defaultPolicy;

    /// <summary>Builds the hook with the given registry + default policy.</summary>
    public ToolGateHook(IToolRegistry registry, ToolGatePolicy defaultPolicy)
    {
        _registry = registry;
        _defaultPolicy = defaultPolicy;
    }

    /// <inheritdoc/>
    public int Order => 200;

    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        var policy = ctx.Metadata.TryGetValue(PolicyOverrideKey, out var p) && p is ToolGatePolicy overridePolicy
            ? overridePolicy
            : _defaultPolicy;

        if (ctx.Metadata.TryGetValue(OfferedKey, out var offeredRaw) && offeredRaw is IEnumerable<string> offered)
        {
            foreach (var name in offered)
            {
                var registered = _registry.GetAll().Where(r => r.Name == name).ToList();
                if (registered.Count == 0)
                    throw new ForbiddenToolException(name, "", "tool not in registry");
                if (registered.Any(r => !policy.IsAllowed(name, r.Category)))
                    throw new ForbiddenToolException(name, "", "denied by policy");
            }
        }

        if (!ctx.Metadata.TryGetValue(RequestedKey, out var raw)
            || raw is not IEnumerable<(string Name, string Version)> requested)
            return default;

        foreach (var (name, version) in requested)
        {
            if (!_registry.TryGet(name, version, out var registration) || registration is null)
                throw new ForbiddenToolException(name, version, "tool not in registry");
            if (!policy.IsAllowed(name, registration.Category))
                throw new ForbiddenToolException(name, version, "denied by policy");
        }
        return default;
    }

    /// <inheritdoc/>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}

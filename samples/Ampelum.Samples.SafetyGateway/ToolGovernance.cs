using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Tools;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>A tool request that was turned down, and which layer did it.</summary>
public sealed record ToolRefusal(string Tool, string Layer, string Reason);

/// <summary>Raised inside a tool call so the model reads the refusal as the tool's result.</summary>
public sealed class ToolRefusedException(string reason) : Exception(reason);

/// <summary>Adapts the trust policy to the dispatcher's policy interface for one agent whose trust level is already known.</summary>
sealed class TrustToolPolicy(TrustLevel level, IAgentTrustPolicy policy) : IToolPolicy
{
    public PolicyDecision Evaluate(ToolManifest tool, AgentIdentity caller, IReadOnlyDictionary<string, object?> args) =>
        policy.EvaluateByTrust(level, tool, caller);
}

/// <summary>
/// The three checks a tool request passes, in order: the registry and identity scope (gate),
/// then mode and trust (policy engine). The dispatcher repeats the policy check when it executes.
/// </summary>
public sealed class ToolGovernance : IDisposable
{
    public const string GateLayer = "gate";
    public const string PolicyLayer = "policy";

    readonly ToolGateHook _gate;
    readonly ToolGatePolicy _scope;
    readonly PolicyEngine _engine;
    readonly ToolLoader _loader;

    ToolGovernance(IToolRegistry registry, AgentIdentity identity, string mode, TrustLevel trust)
    {
        Identity = identity;
        Mode = mode;
        Trust = trust;
        _gate = new ToolGateHook(registry, ToolGatePolicy.Open);
        _scope = ScopePolicy.FromScope(identity.Scope);
        _engine = new PolicyEngine([new ModeAwareToolPolicy(), new TrustToolPolicy(trust, new AgentTrustPolicy())]);
        _loader = new ToolLoader([new CatalogToolSource()]);
    }

    public AgentIdentity Identity { get; }
    public string Mode { get; }
    public TrustLevel Trust { get; }
    public IToolLoader Loader => _loader;

    public static async Task<ToolGovernance> CreateAsync(
        IToolRegistry registry, IAgentTrustStore trusts, AgentIdentity identity, GatewayOptions options, CancellationToken ct)
    {
        var trust = await trusts.GetTrustLevelAsync(identity.AgentType, options.Domain, ct);
        return new ToolGovernance(registry, identity, options.Mode, trust);
    }

    /// <summary>The identity's scope narrows the registry: a registered tool is still refused outside the scope.</summary>
    public async ValueTask<ToolRefusal?> CheckGateAsync(string tool, CancellationToken ct)
    {
        var ctx = new HookContext
        {
            ModuleName = "ToolGate",
            Inputs = new Dictionary<string, object?> { ["tool"] = tool },
        };
        ctx.Set(ToolGateHook.RequestedKey, new[] { (tool, ToolCatalog.Version) });
        ctx.Set(ToolGateHook.PolicyOverrideKey, _scope);
        try
        {
            await _gate.OnBeforeAsync(ctx, ct);
            return null;
        }
        catch (ForbiddenToolException ex)
        {
            return new ToolRefusal(tool, GateLayer, ex.Reason);
        }
    }

    /// <summary>Runs the gate and the policy engine without executing anything.</summary>
    public async Task<ToolRefusal?> PreflightAsync(
        string tool, IReadOnlyDictionary<string, object?> args, IReadOnlyDictionary<string, string[]> allowedAliases, CancellationToken ct)
    {
        if (await CheckGateAsync(tool, ct) is { } refused)
            return refused;
        if (tool == ToolCatalog.Search && args.TryGetValue(ToolCatalog.CredentialArg, out var alias) && alias is not null
            && AliasRule.Refuses(allowedAliases, tool, alias.ToString()))
            return new ToolRefusal(tool, "tool", AliasRule.Reason);
        var manifest = await _loader.TryUnpackAsync(ToolCatalog.QualifiedName(tool), ct);
        if (manifest is null)
            return new ToolRefusal(tool, PolicyLayer, "no manifest");
        var withMode = new Dictionary<string, object?>(args) { [ModeAwareToolPolicy.ModeArgKey] = Mode };
        var decision = _engine.Evaluate(manifest, new PolicyContext { Caller = Identity, Args = withMode });
        return decision.Decision == Decision.Allow
            ? null
            : new ToolRefusal(tool, PolicyLayer, decision.Reason ?? decision.Decision.ToString());
    }

    public ToolDispatcher CreateDispatcher(IToolAuditLog auditLog) =>
        new(_loader, new InMemoryTokenStore(), _engine, Identity, auditLog: auditLog);

    public void Dispose() => _loader.Dispose();
}

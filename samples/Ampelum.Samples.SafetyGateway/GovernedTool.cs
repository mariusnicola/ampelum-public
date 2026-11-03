using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>
/// The tool the model sees. A call goes through the gate, the dispatcher (mode and trust policy, then the handler)
/// and finally the outbound sanitizer, so a result is cleaned before the model reads it.
/// </summary>
public sealed class GovernedTool : AIFunction
{
    readonly ToolDefinition _definition;
    readonly ToolGovernance _governance;
    readonly ToolDispatcher _dispatcher;
    readonly InMemoryToolAuditLog _toolLog;
    readonly OutboundSanitizer _sanitizer;
    readonly SafetyNotes _notes;
    readonly List<ToolRefusal> _refusals;

    public GovernedTool(
        ToolDefinition definition, ToolGovernance governance, ToolDispatcher dispatcher, InMemoryToolAuditLog toolLog,
        OutboundSanitizer sanitizer, SafetyNotes notes, List<ToolRefusal> refusals)
    {
        _definition = definition;
        _governance = governance;
        _dispatcher = dispatcher;
        _toolLog = toolLog;
        _sanitizer = sanitizer;
        _notes = notes;
        _refusals = refusals;
    }

    public override string Name => _definition.Manifest.Name;

    public override string Description => _definition.Manifest.Description;

    public override JsonElement JsonSchema => _definition.Manifest.ArgsSchema ?? default;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
    {
        if (await _governance.CheckGateAsync(Name, ct) is { } gateRefusal)
            throw await RefuseAsync(gateRefusal, ct);

        var args = arguments.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
        // The mode comes from the run, never from the model, so the model cannot ask for a looser one.
        args[ModeAwareToolPolicy.ModeArgKey] = JsonSerializer.SerializeToElement(_governance.Mode);

        var logged = _toolLog.Entries.Count;
        var result = await _dispatcher.InvokeAsync(ToolCatalog.QualifiedName(Name), args, _governance.Identity, ct);
        if (!result.Success)
        {
            var blocked = _toolLog.Entries.Skip(logged).Any(e => e.EventType == "invocation.blocked");
            throw await RefuseAsync(
                new ToolRefusal(Name, blocked ? ToolGovernance.PolicyLayer : "tool", result.Error ?? "refused"), ct);
        }

        var scan = _sanitizer.Sanitize(result.Output ?? "", ScanSource.ToolResult);
        if (scan.Detections.Count > 0)
        {
            await _notes.RecordAsync(SafetyNotes.ToolSanitized, new Dictionary<string, object?>
            {
                ["tool"] = Name,
                ["rules"] = string.Join(",", scan.Detections.Select(d => d.Rule)),
            }, ct);
        }
        await _notes.RecordAsync(SafetyNotes.ToolRan, new Dictionary<string, object?>
        {
            ["tool"] = Name,
            ["arguments"] = _sanitizer.Sanitize(
                string.Join(",", arguments.Keys.Order(StringComparer.Ordinal)), ScanSource.ToolResult).SanitizedContent,
        }, ct);
        return scan.SanitizedContent;
    }

    async Task<ToolRefusedException> RefuseAsync(ToolRefusal refusal, CancellationToken ct)
    {
        // A tool error is free text and can carry a secret, so it is cleaned before the model or the audit file see it.
        var clean = refusal with { Reason = _sanitizer.Sanitize(refusal.Reason, ScanSource.ToolResult).SanitizedContent };
        _refusals.Add(clean);
        await _notes.RecordRefusalAsync(clean, ct);
        return new ToolRefusedException(clean.Reason);
    }
}

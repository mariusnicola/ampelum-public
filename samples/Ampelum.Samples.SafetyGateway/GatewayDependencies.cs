using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>The outside world the gateway talks to. Every member can be swapped for a fake.</summary>
public sealed record GatewayDependencies(
    IChatClient Chat,
    GatewayOptions Options,
    ISecretStore Secrets,
    IAgentIdentityProvider Identity,
    IAgentTrustStore Trust,
    HttpMessageHandler ModerationHandler,
    IEmployeeDirectory Directory,
    HandbookStore Handbook,
    IExecutionSandbox Sandbox)
{
    /// <summary>Receives a record of every module call, with the prompt exactly as it was sent.</summary>
    public ITraceSink? Trace { get; init; }

    /// <summary>Serve model calls from the audit file instead of the model.</summary>
    public bool ReplayAudit { get; init; }
}

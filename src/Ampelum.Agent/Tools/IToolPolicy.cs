namespace SignalGrid.Ampelum.Agent;

/// <summary>Evaluates whether a tool call is permitted, based on manifest and context.
/// Used by PolicyEngine to compose policy decisions from multiple sources.
/// Multiple implementations can be registered; PolicyEngine evaluates them in order.</summary>
public interface IToolPolicy
{
    /// <summary>Evaluate a tool invocation request and return a policy decision.
    /// Implementation should return Allow if no opinion on this tool.</summary>
    PolicyDecision Evaluate(ToolManifest tool, AgentIdentity caller, IReadOnlyDictionary<string, object?> args);
}

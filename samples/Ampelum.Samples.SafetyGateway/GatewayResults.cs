using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.ProviderAdapters;
using SignalGrid.Ampelum.Usage;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>What the caller gets back for one question.</summary>
/// <param name="Text">The reply after outbound sanitizing.</param>
/// <param name="InputFlags">Injection rules found in the question. They are reported, the question is not changed.</param>
/// <param name="Refusals">Tool requests that were turned down during this question.</param>
/// <param name="ToolsRun">Tools that ran and returned a result to the model.</param>
public sealed record GatewayReply(
    string Text,
    IReadOnlyList<string> InputFlags,
    IReadOnlyList<ToolRefusal> Refusals,
    IReadOnlyList<string> ToolsRun);

/// <summary>Token counts per operation and the cost they add up to at the configured rates.</summary>
public sealed record GatewayUsage(UsageSummary Tokens, ChatCostSummary Cost);

/// <summary>The wire format a provider used for the tool calls in a response.</summary>
public enum ProviderFormat
{
    OpenAi,
    Anthropic,
    Gemini,
}

/// <summary>The gateway's decision on one tool call a provider asked for.</summary>
public sealed record ToolCallVerdict(NormalizedToolCall Call, ToolRefusal? Refusal)
{
    public bool Allowed => Refusal is null;
}

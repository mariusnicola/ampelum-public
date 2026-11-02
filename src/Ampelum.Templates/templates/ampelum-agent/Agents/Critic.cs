using SignalGrid.Ampelum.Agents;

namespace Company.Agent.Agents;

/// <summary>Reviews the most recent draft and approves or asks for revision.</summary>
public sealed class Critic : IAgent
{
    public string Name => "Critic";
    public string Role => "critic";

    public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken cancellationToken)
    {
        var verdict = ctx.History.Count == 0 ? "needs more detail" : "approve";
        return ValueTask.FromResult(new AgentMessage(Name, Role, verdict));
    }
}

using SignalGrid.Ampelum.Agents;

namespace Company.Agent.Agents;

/// <summary>Drafts a research note in response to the user's question.</summary>
public sealed class Researcher : IAgent
{
    public string Name => "Researcher";
    public string Role => "researcher";

    public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken cancellationToken)
    {
        var draft = $"Draft: I researched '{ctx.InitialPrompt}' and found three relevant points.";
        return ValueTask.FromResult(new AgentMessage(Name, Role, draft));
    }
}

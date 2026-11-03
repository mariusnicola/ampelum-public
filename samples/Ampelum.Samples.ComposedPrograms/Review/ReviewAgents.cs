using System.Text;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Tools;

namespace Ampelum.Samples.ComposedPrograms.Review;

/// <summary>
/// A panel member backed by one model call. It reads the conversation so far, recalls related earlier
/// remarks from the shared memory, and answers with its opinion.
/// </summary>
public class ReviewAgent(string name, string role, Predict<ReviewSig> predict, string? extraFact = null) : IAgent
{
    public string Name => name;

    public string Role => role;

    public async ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken cancellationToken)
    {
        var conversation = new StringBuilder();
        foreach (var message in ctx.History)
            conversation.Append(message.SenderName).Append(": ").AppendLine(message.Content);
        if (extraFact is not null)
            conversation.Append("Fact: ").AppendLine(extraFact);
        if (ctx.Memory is not null)
        {
            var recalled = await ctx.Memory.QueryAsync(Role, 2, cancellationToken).ConfigureAwait(false);
            foreach (var earlier in recalled)
                conversation.Append("Recalled: ").AppendLine(earlier.Content);
        }
        var result = await predict.ForwardAsync(
            new ReviewSig { Role = Role, Conversation = conversation.ToString().TrimEnd() }, ct: cancellationToken).ConfigureAwait(false);
        return new AgentMessage(Name, Role, result.Value.Opinion) { IsTerminal = IsFinal };
    }

    protected virtual bool IsFinal => false;
}

/// <summary>The last speaker: its answer closes the conversation.</summary>
public sealed class PublisherAgent(string name, Predict<ReviewSig> predict) : ReviewAgent(name, PublisherRole, predict)
{
    public const string PublisherRole = "publisher";

    protected override bool IsFinal => true;
}

/// <summary>Reads the changes-per-area count from the tool registry before it speaks.</summary>
public static class ComplianceFacts
{
    public static async Task<string> CountsAsync(IAgentToolRegistry tools, CancellationToken ct)
    {
        var tool = tools.Find(ChangeCountsTool.ToolName, ChangeCountsTool.ToolVersion)
            ?? throw new InvalidOperationException("The change_counts tool is not registered.");
        var result = await tool.InvokeAsync(ct).ConfigureAwait(false);
        return result.Output ?? "";
    }
}

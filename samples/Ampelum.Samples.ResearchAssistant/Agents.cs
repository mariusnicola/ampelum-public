using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agents;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>Names the agents answer to, and the order the selector walks them in.</summary>
public static class Roles
{
    public const string Planner = "planner";
    public const string Researcher = "researcher";
    public const string Analyst = "analyst";
    public const string Writer = "writer";

    public static readonly IReadOnlyList<string> Order = [Planner, Researcher, Analyst, Writer];
}

/// <summary>Reads the shared conversation memory. A missing message means an earlier agent did not run.</summary>
static class Conversation
{
    public static async Task<AgentMessage> LastFromAsync(ConversationContext ctx, string sender, CancellationToken ct)
    {
        var memory = ctx.Memory ?? throw new InvalidOperationException("The research agents need conversation memory.");
        var recent = await memory.GetRecentAsync(int.MaxValue, ct);
        return recent.LastOrDefault(m => m.SenderName == sender)
            ?? throw new InvalidOperationException($"No message from {sender} in memory.");
    }
}

/// <summary>Turns the question into short search queries.</summary>
sealed class PlannerAgent(IServiceProvider services) : IAgent
{
    public string Name => Roles.Planner;
    public string Role => Roles.Planner;

    public async ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken ct)
    {
        var question = (await Conversation.LastFromAsync(ctx, "system", ct)).Content;
        var predict = services.GetRequiredService<Predict<PlanSignature>>();
        var plan = (await predict.ForwardAsync(new PlanSignature { Question = question }, ct: ct)).Value;
        var queries = SplitQueries(plan.Queries);
        if (queries.Count == 0)
            throw new InvalidOperationException("The planner returned no search queries.");
        return new AgentMessage(Name, Role, string.Join("; ", queries));
    }

    public static List<string> SplitQueries(string raw) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>Runs every planned query against the knowledge base and hands the distinct passages on.</summary>
sealed class ResearcherAgent(KnowledgeBase knowledge, int passagesPerQuery) : IAgent
{
    public const string PassageIdsKey = "passage_ids";

    public string Name => Roles.Researcher;
    public string Role => Roles.Researcher;

    public async ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken ct)
    {
        var plan = await Conversation.LastFromAsync(ctx, Roles.Planner, ct);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var query in PlannerAgent.SplitQueries(plan.Content))
        {
            foreach (var hit in await knowledge.SearchAsync(query, passagesPerQuery, ct))
                seen.TryAdd(hit.Id, hit.Text);
        }
        var text = new StringBuilder();
        foreach (var (id, passage) in seen)
            text.Append('[').Append(id).Append("] ").AppendLine(passage.ReplaceLineEndings(" "));
        return new AgentMessage(Name, Role, text.ToString().TrimEnd())
        {
            Metadata = new Dictionary<string, string> { [PassageIdsKey] = string.Join(",", seen.Keys) },
        };
    }
}

/// <summary>Works out the answer from the passages with the tools: arithmetic, definitions, web, code.</summary>
sealed class AnalystAgent(IServiceProvider services, ResearchTools tools, int maxIterations) : IAgent
{
    public string Name => Roles.Analyst;
    public string Role => Roles.Analyst;

    public async ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken ct)
    {
        var question = (await Conversation.LastFromAsync(ctx, "system", ct)).Content;
        var passages = (await Conversation.LastFromAsync(ctx, Roles.Researcher, ct)).Content;
        var react = new ReAct<AnalystSignature>(services.GetRequiredService<AmpelumContext>(), tools.Functions, maxIterations);
        var result = await react.ForwardAsync(new AnalystSignature { Question = question, Passages = passages }, ct: ct);
        return new AgentMessage(Name, Role, result.Value.Findings);
    }
}

/// <summary>Writes the cited answer from what the earlier agents left in memory and ends the conversation.</summary>
sealed class WriterAgent(IServiceProvider services) : IAgent
{
    public string Name => Roles.Writer;
    public string Role => Roles.Writer;

    public async ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken ct)
    {
        var question = (await Conversation.LastFromAsync(ctx, "system", ct)).Content;
        var passages = (await Conversation.LastFromAsync(ctx, Roles.Researcher, ct)).Content;
        var findings = (await Conversation.LastFromAsync(ctx, Roles.Analyst, ct)).Content;
        var predict = services.GetRequiredService<Predict<WriteSignature>>();
        var written = (await predict.ForwardAsync(
            new WriteSignature { Question = question, Findings = findings, Passages = passages }, ct: ct)).Value;
        return new AgentMessage(Name, Role, written.Answer) { IsTerminal = true };
    }
}

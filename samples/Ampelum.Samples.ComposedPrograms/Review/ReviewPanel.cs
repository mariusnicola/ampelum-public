using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Agents.Tools;

namespace Ampelum.Samples.ComposedPrograms.Review;

/// <summary>
/// Three reviewers talk about the drafted notes: an editor for tone, a compliance reviewer for migration
/// advice on breaking changes, and a publisher who closes the conversation. A selector decides who speaks.
/// </summary>
public sealed class ReviewPanel(
    AmpelumContext ctx,
    IChatClient chat,
    IAgentToolRegistry tools,
    IConversationMemory memory,
    ReleaseNotesOptions options)
{
    public const string EditorRole = "editor";
    public const string ComplianceRole = "compliance";

    public async Task<ReviewOutcome> ReviewAsync(IReadOnlyList<DraftedNote> notes, CancellationToken ct = default)
    {
        var predict = new Predict<ReviewSig>(ctx);
        var counts = await ComplianceFacts.CountsAsync(tools, ct).ConfigureAwait(false);
        IReadOnlyList<IAgent> agents =
        [
            new ReviewAgent("Edith", EditorRole, predict),
            new ReviewAgent("Carl", ComplianceRole, predict, $"changes per area: {counts}"),
            new PublisherAgent("Pia", predict),
        ];
        ISpeakerSelector selector = options.PanelPicksSpeakerWithModel
            ? new LlmDrivenSelector(chat)
            : new RoundRobinSelector();

        var chatRoom = new GroupChat(agents, selector, memory);
        var prompt = "Review these release notes:\n" + string.Join("\n", notes.Select(n => $"{n.Kind}: {n.Note}"));
        var result = await chatRoom.RunAsync(prompt, options.PanelMaxTurns, ct).ConfigureAwait(false);

        var spoken = result.History.Where(m => m.SenderName != "system").ToList();
        var verdict = spoken.LastOrDefault(m => m.Role == PublisherAgent.PublisherRole)?.Content
                      ?? "No decision: " + result.Reason;
        return new ReviewOutcome(
            spoken.Select(m => m.SenderName).ToList(),
            verdict,
            result.Reason == TerminationReason.TerminalMessage
                && verdict.StartsWith("approve", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Three tiers of memory: recent messages, messages searched by meaning, and a rolling summary.</summary>
    public static CompositeConversationMemory BuildMemory(
        IChatClient chat, SignalGrid.Ampelum.Retrieval.IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder, ReleaseNotesOptions options) =>
        new(
            new InMemoryConversationMemory(options.PanelMemoryCapacity),
            new VectorConversationMemory(store, embedder, "panel"),
            new EpisodicMemory(chat, options.EpisodicThreshold, options.EpisodicWindow));
}

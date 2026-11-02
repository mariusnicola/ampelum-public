using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>Sub-question decomposition: ask LM to break query into sub-questions, fan out, dedupe.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SubQuestionRetriever(IRetriever inner, IChatClient chat, SubQuestionOptions options) : IRetriever
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.subquestion");
        var prompt = options.SystemPrompt.Replace("{N}", options.MaxSubQuestions.ToString());
        var hookCtx = AmpelumContext.Ambient is { } ambient
            ? ambient with { Chat = chat }
            : new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var resp = await HookedLmCall.InvokeAsync(
            hookCtx,
            "SubQuestionRetriever.Decompose",
            [new ChatMessage(ChatRole.System, prompt), new ChatMessage(ChatRole.User, query)],
            cancellationToken: ct).ConfigureAwait(false);
        var subQs = resp.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(options.MaxSubQuestions).ToList();
        if (subQs.Count == 0)
        {
            subQs.Add(query);
        }

        var perSub = await Task.WhenAll(subQs.Select(sq => inner.RetrieveAsync(sq, topK, filters, ct))).ConfigureAwait(false);
        var seen = new HashSet<string>();
        var output = new List<RetrievedDocument>();
        foreach (var list in perSub)
        {
            foreach (var d in list)
            {
                if (seen.Add(d.Id))
                {
                    output.Add(d);
                }
            }
        }
        return output.Take(topK).ToList();
    }
}

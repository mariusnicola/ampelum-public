using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Context.Selection;
using SignalGrid.Ampelum.Context.Chapters;
using SignalGrid.Ampelum.Retrieval;

namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Default selector that returns the last N events for talk mode.</summary>
public sealed class TalkSelector : IContextSelector
{
    private const int DefaultRecentCount = 50;

    /// <inheritdoc />
    public async Task<SelectionPlan> PlanAsync(IEventStore store, SelectionRequest request, CancellationToken ct)
    {
        var allEvents = new List<EventEntry>();
        await foreach (var e in store.ReadAsync(new EventQuery(), ct).ConfigureAwait(false))
            allEvents.Add(e);

        var recent = allEvents
            .OrderByDescending(e => e.Turn)
            .ThenByDescending(e => e.Seq)
            .Take(DefaultRecentCount)
            .Select(e => ToSelectedInfo(e))
            .ToList();

        var prefix = new ContextPrefix(
            ModeBanner: $"// Mode: {request.Mode}\n",
            GoalAnchor: string.Empty,
            ChapterIndex: string.Empty);

        var estimatedTokens = recent.Sum(e => e.TokenCount);

        return new SelectionPlan(
            prefix,
            recent.AsReadOnly(),
            new List<DecisionTraceEntry>().AsReadOnly(),
            estimatedTokens,
            request.Mode);
    }

    /// <summary>Projects an EventEntry into a SelectedEventInfo for the selection plan.</summary>
    private static SelectedEventInfo ToSelectedInfo(EventEntry e)
    {
        var (body, source) = e switch
        {
            UserMessage u => (u.Text, "user"),
            AssistantText a => (a.Text, "assistant"),
            ToolCall t => ($"Tool call: {t.Name}({t.ArgsJson})", "tool_call"),
            ToolResult r => (r.Body, "tool_result"),
            ModeTransition m => ($"Mode: {m.From} -> {m.To}", "mode_transition"),
            ChapterBoundary c => ($"Chapter boundary: {c.ChapterId}", "chapter_boundary"),
            Decision d => ($"Decision: {d.Statement}", "decision"),
            _ => ($"Event: {e.GetType().Name}", "other")
        };
        var tokenCount = (body.Length / 4) + 1; // rough estimate
        return new SelectedEventInfo(e.Key, e.GetType().Name, body, tokenCount, source);
    }
}
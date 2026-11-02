using SignalGrid.Ampelum.Context;
using SignalGrid.Ampelum.Context.Selection;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>
/// Pre-turn context selector that uses the CodeIntelligence indexer pipeline
/// to assemble relevant code context from the user's query.
/// Falls through to default behavior when the index is cold or returns no content.
/// </summary>
public sealed class CodeIntelligenceContextSelector : IContextSelector
{
    readonly IndexerPipeline _pipeline;

    public CodeIntelligenceContextSelector(IndexerPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public async Task<SelectionPlan> PlanAsync(IEventStore store, SelectionRequest request, CancellationToken ct)
    {
        // Use the user's query to assemble code context
        var context = _pipeline.AssembleContext(request.UserText);
        if (!context.HasContent)
        {
            // Fall through to default behavior
            return new SelectionPlan(
                new ContextPrefix(ModeBanner: $"// Mode: {request.Mode}\n", GoalAnchor: "", ChapterIndex: ""),
                Array.Empty<SelectedEventInfo>(),
                Array.Empty<DecisionTraceEntry>(),
                0,
                request.Mode);
        }

        var info = new SelectedEventInfo(
            EventKey: "__code_intelligence__",
            EventType: "code_intelligence",
            Body: context.Format(),
            TokenCount: context.EstimatedTokens,
            Source: "code_index");

        return new SelectionPlan(
            new ContextPrefix(ModeBanner: $"// Mode: {request.Mode}\n", GoalAnchor: "", ChapterIndex: ""),
            new[] { info },
            Array.Empty<DecisionTraceEntry>(),
            context.EstimatedTokens,
            request.Mode);
    }
}

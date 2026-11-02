namespace SignalGrid.Ampelum.Agent;

/// <summary>Decomposes a high-level portfolio intent into a task DAG for a specific workspace.
/// The interface lives here (zero LLM dep); the LLM-bound implementation lives in Synthesis.</summary>
public interface IPortfolioIntentDecomposer
{
    /// <summary>Given an intent and workspace summary, produce a DAG of task definitions.
    /// Returns empty array if the intent is already satisfied.</summary>
    Task<TaskDefinition[]> DecomposeAsync(
        string intent,
        ProjectSummary projectSummary,
        WorkspaceContext workspace,
        CancellationToken ct = default);
}
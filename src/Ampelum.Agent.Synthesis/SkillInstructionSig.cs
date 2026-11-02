using SignalGrid.Ampelum;

namespace SignalGrid.Ampelum.Agent.Synthesis;

[Signature("Given a task description and execution history, write an improved system prompt for a skill.")]
public sealed record SkillInstructionSig(
    string TaskDescription,
    string ExecutionHistory,
    string ImprovedSystemPrompt
) : ISignature
{
    [Input] public string TaskDescription { get; } = TaskDescription;
    [Input] public string ExecutionHistory { get; } = ExecutionHistory;
    [Output] public string ImprovedSystemPrompt { get; } = ImprovedSystemPrompt;
}

namespace SignalGrid.Ampelum.Agents.SpeakerSelectors;

/// <summary>
/// Internal signature used by <see cref="LlmDrivenSelector"/> to ask the coordinator LLM
/// which named agent should speak next.
/// </summary>
[Signature("Pick the next speaker name from the available list.")]
internal sealed record SelectSpeakerSig : ISignature
{
    [Input] public required string History { get; init; }
    [Input] public required string AvailableAgents { get; init; }
    [Output] public string NextSpeaker { get; init; } = "";
}

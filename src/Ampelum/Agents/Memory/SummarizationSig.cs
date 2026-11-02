namespace SignalGrid.Ampelum.Agents.Memory;

/// <summary>
/// Internal signature used by <see cref="EpisodicMemory"/> to LLM-summarise an old conversation segment.
/// </summary>
[Signature("Summarize this conversation segment in 2-3 sentences preserving key facts.")]
internal sealed record SummarizationSig : ISignature
{
    [Input] public required string Conversation { get; init; }
    [Output] public string Summary { get; init; } = "";
}

using SignalGrid.Ampelum;

// Global namespace on purpose: the per-signature initializer emits the type name
// unqualified, so a namespace starting with "Ampelum" does not resolve from
// SignalGrid.Ampelum.Generated.
[Signature("Summarize the passage in one sentence.")]
public sealed record Summarize : ISignature
{
    [Input(Desc = "the text to summarize")]
    public required string Passage { get; init; }

    [Output(Desc = "one sentence")]
    public string Summary { get; init; } = "";
}

using SignalGrid.Ampelum;

namespace Ampelum.Samples.ChatFrontend;

/// <summary>
/// One typed answer for the chat widget. The same signature serves the one-shot and the streamed
/// path, because the semantic cache key is built from the module name and the input names, not
/// from the signature type (see the README).
/// </summary>
[Signature("Answer the customer's question in one or two sentences, then suggest one follow-up question.")]
public sealed record ReplySignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
    [Output] public string FollowUp { get; init; } = "";
}

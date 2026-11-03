using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.ChatFrontend;

/// <summary>Scripted models for fake mode. The queues are consumed in the order <see cref="ChatDemo"/> asks.</summary>
public sealed class FakeScript
{
    public const string PrimaryModelId = "scripted-primary";
    public const string FallbackModelId = "scripted-fallback";

    public static readonly string[] StreamedTokens = ["Standard ", "delivery ", "takes ", "3 to 5 ", "business days."];

    public FakeChatClient Primary { get; } = new FakeChatClient()
        .EnqueueStreamingChunks(StreamedTokens)
        .EnqueueStreamingOutputs(new ReplySignature
        {
            Question = "",
            Answer = "Returns are accepted for 30 days.",
            FollowUp = "Do you need a return label?",
        })
        .EnqueueOutputs(new ReplySignature
        {
            Question = "",
            Answer = "We ship to every EU country.",
            FollowUp = "Which country do you live in?",
        })
        .EnqueueText("We are open from 9 to 17 on weekdays.");

    public FakeChatClient Fallback { get; } = new FakeChatClient()
        .EnqueueText("Gift cards never expire.");

    public ChatBackend CreateBackend() => new(Primary, PrimaryModelId, Fallback, FallbackModelId);

    /// <summary>Makes the next non-streaming call to the primary model fail.</summary>
    public void FailPrimaryNextCall() => Primary.EnqueueError(new HttpRequestException("scripted primary outage"));
}

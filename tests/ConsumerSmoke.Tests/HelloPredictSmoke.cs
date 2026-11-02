using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace ConsumerSmoke.Tests;

/// <summary>Signature consumed via the Ampelum nupkg. The source generator
/// shipped in Ampelum.SourceGenerator pack must produce the metadata cache
/// for this type at build-time inside the consumer assembly.</summary>
[Signature("Reply to a greeting with a one-word reply.")]
public sealed record ConsumerHelloSig : ISignature
{
    /// <summary>Input greeting.</summary>
    [Input] public string Greeting { get; init; } = "";
    /// <summary>Output reply.</summary>
    [Output] public string Reply { get; init; } = "";
}

/// <summary>
/// Scenario A — Hello-Predict end-to-end via FakeChatClient.
/// Verifies: Ampelum core API, ChatAdapter parse path, FakeChatClient from
/// Ampelum.Testing pack, the ChatAdapter "[[ ## Field ## ]]" marker contract.
/// </summary>
public sealed class HelloPredictSmoke
{
    [Fact]
    public async Task Predict_Forward_ReturnsParsedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeChatClient();
        fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");

        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

        var predict = new Predict<ConsumerHelloSig>(ctx);
        var result = await predict.ForwardAsync(new ConsumerHelloSig { Greeting = "hello" }, ct: ct);

        result.Value.Reply.ShouldBe("hi");
    }
}

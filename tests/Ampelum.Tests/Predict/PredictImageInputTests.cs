using System;
using System.Linq;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.PredictImageInput;

[Collection(nameof(ActivityListenerCollection))]
public class PredictImageInputTests
{
    [Signature("Describe what is in the image and answer the question.")]
    public sealed record VisionSig : ISignature
    {
        [Input] public required byte[] Image { get; init; }
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("Describe the URI image and answer.")]
    public sealed record VisionUriSig : ISignature
    {
        [Input] public required Uri Image { get; init; }
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("Generate an image.")]
    public sealed record GenSig : ISignature
    {
        [Input] public required string Prompt { get; init; }
        [Output] public byte[] Picture { get; init; } = [];
    }

    static AmpelumContext Ctx(IChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task Byte_array_input_emits_data_content()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nblue");
        var p = new Predict<VisionSig>(Ctx(chat));
        var pred = await p.ForwardAsync(new VisionSig { Image = bytes, Question = "what color?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("blue");
        var lastMsg = chat.RecordedCalls[0].Messages[^1];
        var dataContent = lastMsg.Contents.OfType<DataContent>().FirstOrDefault();
        dataContent.ShouldNotBeNull();
        dataContent.Data.ToArray().ShouldBe(bytes);
        // Question text is also present
        var combinedText = string.Concat(lastMsg.Contents.OfType<TextContent>().Select(t => t.Text));
        combinedText.ShouldContain("what color?");
    }

    [Fact]
    public async Task Uri_input_emits_uri_content()
    {
        var u = new Uri("https://example.com/img.png");
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nred");
        var p = new Predict<VisionUriSig>(Ctx(chat));
        var pred = await p.ForwardAsync(new VisionUriSig { Image = u, Question = "what color?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe("red");
        var lastMsg = chat.RecordedCalls[0].Messages[^1];
        var uriContent = lastMsg.Contents.OfType<UriContent>().FirstOrDefault();
        uriContent.ShouldNotBeNull();
        uriContent.Uri.ShouldBe(u);
    }

    [Fact]
    public async Task Byte_array_output_throws_not_supported()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Picture ## ]]\nfoo");
        var p = new Predict<GenSig>(Ctx(chat));
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await p.ForwardAsync(new GenSig { Prompt = "draw" }, ct: TestContext.Current.CancellationToken));
    }
}

using System.Net;
using System.Text;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.ContractTests;

public sealed class ChatAdapterOverHttpTests
{
    [Signature("")]
    public sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public async Task RoundTrip_Through_MEAI_OpenAI_Adapter()
    {
        var handler = new CannedHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    OpenAiChatCompletionBody("[[ ## Answer ## ]]\nParis"),
                    Encoding.UTF8,
                    "application/json"),
            });

        IChatClient chat = OpenAiTestHarness.BuildChatClient(handler);
        var predict = Predict.Create<Qa>(chat);
        var r = await predict.ForwardAsync(new Qa { Question = "Capital of France?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Paris");

        handler.Requests.Count.ShouldBe(1);  // single LM call invariant
    }

    static string OpenAiChatCompletionBody(string content) =>
        $$"""
        {
          "id":"chatcmpl-1","object":"chat.completion","created":1,
          "model":"gpt-4o-mini",
          "choices":[{"index":0,"message":{"role":"assistant","content":{{System.Text.Json.JsonSerializer.Serialize(content)}}},"finish_reason":"stop"}],
          "usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}
        }
        """;
}

using System.Net;
using System.Text;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.ContractTests;

public sealed class JsonAdapterOverHttpTests
{
    [Signature("")]
    public sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Fact]
    public async Task RoundTrip_Through_MEAI_OpenAI_Adapter_WithJsonAdapter()
    {
        // The JSON adapter sends ChatOptions.ResponseFormat, which the canned handler ignores.
        // The handler returns the JSON object in the message content, which JsonAdapter parses.
        const string jsonContent = """{"answer":"Paris"}""";

        var handler = new CannedHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    OpenAiChatCompletionBody(jsonContent),
                    Encoding.UTF8,
                    "application/json"),
            });

        IChatClient chat = OpenAiTestHarness.BuildChatClient(handler);
        var predict = Predict.Create<Qa>(chat, adapter: new JsonAdapter());
        var r = await predict.ForwardAsync(new Qa { Question = "Capital of France?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Paris");

        handler.Requests.Count.ShouldBe(1);  // single LM call invariant
    }

    static string OpenAiChatCompletionBody(string content) =>
        $$"""
        {
          "id":"chatcmpl-2","object":"chat.completion","created":1,
          "model":"gpt-4o-mini",
          "choices":[{"index":0,"message":{"role":"assistant","content":{{System.Text.Json.JsonSerializer.Serialize(content)}}},"finish_reason":"stop"}],
          "usage":{"prompt_tokens":12,"completion_tokens":5,"total_tokens":17}
        }
        """;
}

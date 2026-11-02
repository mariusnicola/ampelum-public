using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.ReActIntegration;

[Collection("ReActTypedTool")]
public class ReActTypedToolTests
{
    [Signature("")] public sealed record QA : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public int Answer { get; init; }
    }

    sealed class AddTool : IReActTool
    {
        public string Name => "add";
        public string Description => "Adds two integers a + b.";
        public string ParametersJsonSchema =>
            """{"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"required":["a","b"]}""";
        public ValueTask<string> InvokeAsync(string argumentsJson, CancellationToken ct)
        {
            var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var a = args.RootElement.GetProperty("a").GetInt32();
            var b = args.RootElement.GetProperty("b").GetInt32();
            return ValueTask.FromResult((a + b).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    static AmpelumContext Ctx(FakeChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task TypedTool_invocation_routes_through_react_loop()
    {
        var firstMsg = new ChatMessage(ChatRole.Assistant, "");
        firstMsg.Contents.Add(new FunctionCallContent("c1", "add",
            new Dictionary<string, object?> { ["a"] = 2, ["b"] = 3 }));
        var sink = new InMemoryTraceSink();
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(firstMsg))
            .EnqueueText("final_answer: 5");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);

        var react = new ReAct<QA>(ctx, typedTools: [new AddTool()]);
        var pred = await react.ForwardAsync(new QA { Question = "2+3?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe(5);
        var toolCalls = (IReadOnlyList<ToolCallRecord>)sink.Records[0].Metadata[TraceKeys.ToolCalls]!;
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].FunctionName.ShouldBe("add");
        toolCalls[0].Result?.ToString().ShouldBe("5");
    }

    [Fact]
    public async Task TypedTool_exposes_json_schema_to_react_options()
    {
        var firstMsg = new ChatMessage(ChatRole.Assistant, "");
        firstMsg.Contents.Add(new FunctionCallContent("c1", "add",
            new Dictionary<string, object?> { ["a"] = 1, ["b"] = 1 }));
        var sink = new InMemoryTraceSink();
        var chat = new FakeChatClient()
            .EnqueueRaw(new ChatResponse(firstMsg))
            .EnqueueText("final_answer: 2");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var react = new ReAct<QA>(ctx, typedTools: [new AddTool()]);
        await react.ForwardAsync(new QA { Question = "1+1?" }, ct: TestContext.Current.CancellationToken);
        // Verify the AIFunction wrapper exposes the schema correctly.
        var lastCall = chat.RecordedCalls[0];
        var tools = lastCall.Options?.Tools;
        tools.ShouldNotBeNull();
        var fn = tools!.OfType<AIFunction>().FirstOrDefault(t => t.Name == "add");
        fn.ShouldNotBeNull();
        // Touch JsonSchema to ensure the lazy getter executes.
        fn.JsonSchema.ToString().ShouldContain("integer");
    }

    [Fact]
    public async Task Existing_aifunction_constructor_still_works()
    {
        var chat = new FakeChatClient().EnqueueText("final_answer: 0");
        var aifn = AIFunctionFactory.Create(() => "x", "x", "x");
        var react = new ReAct<QA>(Ctx(chat), tools: [aifn]);
        var pred = await react.ForwardAsync(new QA { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Value.Answer.ShouldBe(0);
    }
}

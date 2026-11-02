# ReAct with a Calculator Tool

`ReAct<TSig>` runs a self-driven tool-call loop. The LM may emit
`FunctionCallContent` to invoke a registered `AIFunction`, observe the
result, and then emit `final_answer:` to terminate.

```csharp
// ampelum:recipe react-with-tool
var add = AIFunctionFactory.Create((int a, int b) => a + b, "add", "Adds two integers.");

var toolMsg = new ChatMessage(ChatRole.Assistant, "");
toolMsg.Contents.Add(new FunctionCallContent("c1", "add",
    new Dictionary<string, object?> { ["a"] = 2, ["b"] = 3 }));

var fake = new FakeChatClient()
    .EnqueueRaw(new ChatResponse(toolMsg))
    .EnqueueText("final_answer: 5");

var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var react = new ReAct<MathQa>(ctx, tools: [add]);
var pred = await react.ForwardAsync(new MathQa { Question = "2+3" }, ct: ct);
pred.Value.Answer.ShouldBe("5");
```

The first response is a tool call; ReAct invokes `add(2,3)`, feeds the result
back, and the second response is the final answer.

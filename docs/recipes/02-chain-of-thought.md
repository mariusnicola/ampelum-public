# Chain-of-Thought

`ChainOfThought<TSig>` prepends a `reasoning` field to the output schema. The
LM emits its step-by-step reasoning before the typed `[Output]` fields, which
the adapter parses out and exposes via `Prediction.Reasoning()`.

```csharp
// ampelum:recipe chain-of-thought
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## reasoning ## ]]\nbecause it is\n[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var cot = new ChainOfThought<HelloSig>(ctx);
var r = await cot.ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
r.Reasoning().ShouldBe("because it is");
```

Expected: `Reply == "hi"`, `Reasoning() == "because it is"`.

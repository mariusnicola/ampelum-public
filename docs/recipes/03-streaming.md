# Streaming

`Predict<TSig>.StreamAsync` yields `PredictionUpdate<TSig>` items as the LM
emits tokens, and concludes with a terminal update (`IsFinal == true`) whose
`Value` matches what `ForwardAsync` would have returned.

```csharp
// ampelum:recipe streaming
var fake = new FakeChatClient();
fake.EnqueueStreamingChunks("[[ ## Reply ## ]]\n", "hi", "\n[[ ## completed ## ]]");
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<HelloSig>(ctx);
string? final = null;
await foreach (var u in p.StreamAsync(new HelloSig("?"), ct: ct))
{
    if (u.IsFinal) final = u.Value!.Reply;
}
final.ShouldBe("hi");
```

Expected: `final == "hi"` after the terminal update.

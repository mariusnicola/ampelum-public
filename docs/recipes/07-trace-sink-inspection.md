# Trace Sink Inspection

`InMemoryTraceSink` captures every `TraceRecord` the runtime writes during a
forward — useful for offline debugging and for asserting side-effects without
spinning up a real OTel pipeline.

```csharp
// ampelum:recipe trace-sink-inspection
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var sink = new InMemoryTraceSink();
var ctx = new AmpelumContext(fake, new ChatAdapter(), sink, NullLoggerFactory.Instance);
var p = new Predict<HelloSig>(ctx);
await p.ForwardAsync(new HelloSig("?"), ct: ct);
sink.Records.Count.ShouldBeGreaterThan(0);
```

Expected: one or more `TraceRecord`s emitted; inspect `sink.Records` for the
captured key/value pairs.

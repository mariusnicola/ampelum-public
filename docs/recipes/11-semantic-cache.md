# Semantic Cache

`SemanticCacheHook` wraps an `ISemanticCache` and short-circuits the LM
call when the inputs match a previously-seen entry. The first forward
populates the cache; the second forward hits and skips the LM entirely.

```csharp
// ampelum:recipe semantic-cache
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var cache = new InMemorySemanticCache(capacity: 16);
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
{
    Hooks = new IHook[] { new SemanticCacheHook(cache) },
};
var p = new Predict<HelloSig>(ctx);
var first = await p.ForwardAsync(new HelloSig("?"), ct: ct);
first.Value.Reply.ShouldBe("hi");
// Second call short-circuits — no second EnqueueText needed.
var second = await p.ForwardAsync(new HelloSig("?"), ct: ct);
second.Value.Reply.ShouldBe("hi");
fake.RecordedCalls.Count.ShouldBe(1);
```

The single `RecordedCalls` proves the second forward did not hit the LM.

# Rate Limiting

`RateLimitedChatClient` decorates an inner `IChatClient` with a partitioned
token-bucket. When a permit cannot be acquired within
`RateLimitingOptions.AcquireTimeout`, the decorator throws
`RateLimitedException` from `GetResponseAsync`.

```csharp
// ampelum:recipe rate-limit
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var opts = new RateLimitingOptions
{
    RequestsPerMinute = 60,
    BurstMultiplier = 5.0,
    AcquireTimeout = TimeSpan.FromSeconds(1),
};
var rl = new RateLimitedChatClient(fake, opts);
var ctx = new AmpelumContext(rl, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<HelloSig>(ctx);
var r = await p.ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
```

Under burst, the partitioned bucket allows up to `RequestsPerMinute *
BurstMultiplier` requests; subsequent calls block on permit-acquire and
throw if the timeout elapses.

# Retry on Parse Failure

`RetryHook` re-issues the LM call with a corrective prompt when the first
response fails to parse (`AdapterParseException`). The hook bounds recovery
attempts at `maxRecoveryAttempts`.

```csharp
// ampelum:recipe retry-on-parse-failure
var fake = new FakeChatClient();
fake.EnqueueText("garbage no marker");
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
{
    Hooks = new IHook[] { new RetryHook(maxRecoveryAttempts: 2) },
};
var p = new Predict<HelloSig>(ctx);
var r = await p.ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
```

Expected: first response is unparseable; retry with corrective prompt yields
`"hi"`.

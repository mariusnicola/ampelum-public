# Audit Chain Validation

Every forward call the `AuditHook` observes appends one `AuditEntry` to the
registered `IAuditSink`, hash-chained to the prior row via `PrevHash` /
`EntryHash`. `AuditChainVerifier.VerifyAsync(sink, ct)` re-walks the chain
and reports whether any row has been tampered with.

```csharp
// ampelum:recipe audit-chain-validation
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var sink = new InMemoryAuditSink();
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
{
    Hooks = new IHook[] { new AuditHook(sink) },
};
var p = new Predict<HelloSig>(ctx);
await p.ForwardAsync(new HelloSig("?"), ct: ct);
var result = await AuditChainVerifier.VerifyAsync(sink, ct);
result.IsValid.ShouldBeTrue();
sink.NextSequence.ShouldBe(1L);
```

Expected: one audit row whose chain validates clean (`IsValid == true`,
`NextSequence == 1`).

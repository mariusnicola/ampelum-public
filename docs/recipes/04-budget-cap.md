# Budget Cap

`Budget.Create().WithCallLimit(n).Build()` constructs an `IBudget` that
`BudgetHook` enforces as a circuit breaker. When the cap is reached, the next
forward throws `BudgetExceededException` from the hook's `OnBefore` — no live
LM call is issued.

```csharp
// ampelum:recipe budget-cap
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var budget = Budget.Create().WithCallLimit(1).Build();
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
{
    Hooks = new IHook[] { new BudgetHook(budget) },
};
var p = new Predict<HelloSig>(ctx);
var r = await p.ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
await Should.ThrowAsync<BudgetExceededException>(
    async () => await p.ForwardAsync(new HelloSig("?"), ct: ct));
```

Expected: first call returns `"hi"`; second call throws `BudgetExceededException`.

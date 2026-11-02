# Hook Pipeline Composition

`AmpelumContext.Hooks` is an ordered list. `HookPipeline` runs `OnBefore`
ascending by `Order` and `OnAfter` descending. A hook that writes
`HookContext.Outputs` inside `OnBefore` short-circuits the LM call — the
remaining `OnBefore` hooks are skipped, the chat client is never invoked,
and the parsed outputs are surfaced as the prediction value.

This recipe wires three hooks — a `BudgetHook`, an `AuditHook`, and a
custom `RecipeLoggingHook` — then re-runs the pipeline with a
`RecipeShortCircuitHook` whose `Order = -1` makes it execute first and
suppress the live call.

```csharp
// ampelum:recipe hook-composition
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");

var budget = Budget.Create().WithCallLimit(5).Build();
var sink = new InMemoryAuditSink();
var logger = new RecipeLoggingHook { Order = 100 };

var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
{
    Hooks = new IHook[]
    {
        new BudgetHook(budget),         // Order = 0 (default)
        new AuditHook(sink),            // Order = 0 (default, runs after budget by registration order)
        logger,                         // Order = 100, runs last in OnBefore, first in OnAfter
    },
};

var p = new Predict<HelloSig>(ctx);
var r = await p.ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
logger.Events.ShouldContain("before:Predict");
logger.Events.ShouldContain("after:Predict");
sink.NextSequence.ShouldBe(1L);

// Short-circuit: a second context whose first-running hook fills Outputs in OnBefore.
// The remaining OnBefore hooks AND the LM call are skipped — fake stays untouched.
var cached = new Dictionary<string, object?> { ["Reply"] = "cached" };
var shortCircuit = new RecipeShortCircuitHook(cached, order: -1);
var loggerB = new RecipeLoggingHook { Order = 100 };
var ctx2 = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
{
    Hooks = new IHook[] { shortCircuit, loggerB },
};
var p2 = new Predict<HelloSig>(ctx2);
var r2 = await p2.ForwardAsync(new HelloSig("?"), ct: ct);
r2.Value.Reply.ShouldBe("cached");
loggerB.Events.ShouldNotContain("before:Predict"); // Order=100 hook never reached OnBefore
loggerB.Events.ShouldContain("after:Predict");      // OnAfter still fires post-short-circuit
fake.RecordedCalls.Count.ShouldBe(1);               // only the first scenario hit the wire
```

The `Order` field controls ascending OnBefore / descending OnAfter walking
order; setting `Order` to a negative value lets a cache or replay hook
intercept before any user-registered hook sees the call.

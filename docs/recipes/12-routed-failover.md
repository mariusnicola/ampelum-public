# Routed Failover

`RoutedChatClient` decorates a list of `ProviderEndpoint` with a pluggable
`IRoutingStrategy`. The `Failover` strategy keeps the first endpoint as
primary and walks the rest as a fallback chain on transport failure.

```csharp
// ampelum:recipe routed-failover
var primary = new AlwaysThrowingChatClient();
var secondary = new FakeChatClient();
secondary.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");

var router = new RoutedChatClient(
    new[]
    {
        new ProviderEndpoint("primary", primary),
        new ProviderEndpoint("secondary", secondary),
    },
    new Failover());

var ctx = new AmpelumContext(router, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<HelloSig>(ctx);
var r = await p.ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
secondary.RecordedCalls.Count.ShouldBe(1);
```

The router tries primary, sees the failure, and walks to secondary which
succeeds. Both `RecordedCalls` lists confirm the chain was exercised.

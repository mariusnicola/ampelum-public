# Hello, Predict

A minimal recipe that drives a `Predict<TSig>` against a `FakeChatClient`.
The `// ampelum:recipe <slug>` tag is what pins the block as an executable
test; an extracted file <code>HelloPredict.g.cs</code> lives under
<code>tests/Ampelum.Recipes.Tests/Generated/</code>.

```csharp
// ampelum:recipe hello-predict
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<HelloSig>(ctx);
var r = await p.ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
```

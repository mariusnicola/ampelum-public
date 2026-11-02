# Source-Generator Signature Metadata

Ampelum ships a Roslyn source generator that emits compile-time metadata
for any record marked `[Signature]`. Default mode pre-populates
`SignatureMetadataCache` so the first `For<TSig>()` call avoids the
reflection build path. Adding `[assembly: GenerateSignatureMetadata]` to
a consuming assembly switches the generator into AOT-strict mode: in
addition to the metadata, it emits delegate-based property accessors via
`SignatureMetadataCache.RegisterAccessors<TSig>(...)`, so the runtime
forward-pass can read `[Input]` field values without `PropertyInfo.GetValue`.

The recipe asserts the metadata that the generator (or, in this assembly,
the reflection fallback) produces for the shared `HelloSig` fixture, then
checks the AOT-strict accessor probe.

```csharp
// ampelum:recipe source-generator
var meta = SignatureMetadataCache.For<HelloSig>();
meta.Inputs.Count.ShouldBe(1);
meta.Inputs[0].Name.ShouldBe("Greeting");
meta.Inputs[0].ClrType.ShouldBe(typeof(string));
meta.Outputs.Count.ShouldBe(1);
meta.Outputs[0].Name.ShouldBe("Reply");
meta.Instructions.ShouldBe("Reply to a greeting with a one-word reply.");

// AOT-strict probe. `Ampelum.Recipes.Tests` is NOT marked
// [assembly: GenerateSignatureMetadata], so the accessor table is empty
// and the runtime falls back to PropertyInfo.GetValue. Adding the
// assembly attribute would flip TryGetAccessors to return true and
// surface a Func<HelloSig, object?>[] sized to meta.Inputs.Count.
var hasAccessors = SignatureMetadataCache.TryGetAccessors(typeof(HelloSig), out var accessors);
hasAccessors.ShouldBeFalse();
accessors.ShouldBeNull();

// The signature still drives a normal forward pass — the source generator's
// pre-population path is purely a startup-cost optimisation.
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var r = await new Predict<HelloSig>(ctx).ForwardAsync(new HelloSig("?"), ct: ct);
r.Value.Reply.ShouldBe("hi");
```

To enable AOT-strict mode in your own consuming assembly, add a single
attribute to any file in the project:

```csharp
[assembly: Ampelum.GenerateSignatureMetadata]
```

After rebuild, `TryGetAccessors(typeof(YourSig), out var d)` returns
`true` and `d` is a `Func<YourSig, object?>[]` whose length matches the
signature's `[Input]` field count, in declaration order.

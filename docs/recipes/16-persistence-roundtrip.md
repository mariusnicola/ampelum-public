# Persistence Round-Trip

`JsonProgramPersistence.SaveAsync` writes an `OptimizedProgram<TProgram>`
to a versioned JSON wire format; `LoadPredictAsync<TSig>` rebinds it with
a fresh `Predict<TSig>` and a caller-supplied `AmpelumContext`. Demos
and instruction overrides are preserved across the round-trip.

```csharp
// ampelum:recipe persistence-roundtrip
var fake = new FakeChatClient();
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

var demo = new Example(
    Inputs:  new Dictionary<string, object?> { ["Greeting"] = "?" },
    Outputs: new Dictionary<string, object?> { ["Reply"] = "hi" });
var demosBySubmodule = new Dictionary<string, IReadOnlyList<Example>>
{
    ["Predict"] = new[] { demo },
};
var optimized = new OptimizedProgram<Predict<HelloSig>>(
    Program: new Predict<HelloSig>(ctx),
    DemosBySubmodule: demosBySubmodule,
    InstructionOverrides: new Dictionary<string, string>());

var persistence = new JsonProgramPersistence();
var path = Path.Combine(Path.GetTempPath(), $"opt-{Guid.NewGuid():N}.json");
try
{
    await persistence.SaveAsync(optimized, path, ct);
    var loaded = await persistence.LoadPredictAsync<HelloSig>(path, ctx, ct);
    loaded.DemosBySubmodule.ContainsKey("Predict").ShouldBeTrue();
    loaded.DemosBySubmodule["Predict"].Count.ShouldBe(1);
    loaded.DemosBySubmodule["Predict"][0].Inputs["Greeting"].ShouldBe("?");
    loaded.DemosBySubmodule["Predict"][0].Outputs["Reply"].ShouldBe("hi");
}
finally
{
    if (File.Exists(path)) File.Delete(path);
}
```

The wire format pins a version field; `LoadPredictAsync` throws
`ProgramPersistenceException` on version mismatch.

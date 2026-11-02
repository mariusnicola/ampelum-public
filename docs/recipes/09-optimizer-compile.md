# Optimizer Compile + Persist

`BootstrapFewShot<TProgram>` runs the program over a labelled trainset,
captures per-submodule traces, and emits an `OptimizedProgram<TProgram>`
whose `DemosBySubmodule` can be serialised via `JsonProgramPersistence`.

```csharp
// ampelum:recipe optimizer-compile
var fake = new FakeChatClient();
for (int i = 0; i < 5; i++)
    fake.EnqueueText("[[ ## Reply ## ]]\nhi\n[[ ## completed ## ]]");
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var trainset = new List<Example>();
for (int i = 0; i < 5; i++)
    trainset.Add(new Example(
        Inputs:  new Dictionary<string, object?> { ["Greeting"] = "?" },
        Outputs: new Dictionary<string, object?> { ["Reply"] = "hi" }));

var opt = new BootstrapFewShot<Predict<HelloSig>>(
    programRunner: async (prog, ex, runCtx, t) =>
    {
        await prog.ForwardAsync(new HelloSig("?"), ct: t);
    },
    maxBootstrappedDemos: 4);

SignalGrid.Ampelum.Optimizers.IMetric metric = new ExactMatchOptMetric();
var optimized = await opt.CompileAsync(new Predict<HelloSig>(ctx), trainset, metric, ctx, ct: ct);

var path = Path.Combine(Path.GetTempPath(), $"opt-{Guid.NewGuid():N}.json");
var persistence = new JsonProgramPersistence();
await persistence.SaveAsync(optimized, path, ct);
File.Exists(path).ShouldBeTrue();
File.Delete(path);
```

`Example` is a plain `(inputs, outputs)` record; the runner just calls
the program once per example. `ExactMatchOptMetric` is a tiny test stub.

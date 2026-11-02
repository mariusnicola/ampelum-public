# Evaluate Harness

`Evaluate<TSig>` runs a prediction program over a list of
`LabeledExample<TSig>`, scoring each via an `IMetric<TSig>` with a
configurable degree of parallelism, and aggregates the results.

```csharp
// ampelum:recipe evaluate-harness
var fake = new FakeChatClient();
for (int i = 0; i < 5; i++)
    fake.EnqueueText("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<MathQa>(ctx);

var examples = new List<LabeledExample<MathQa>>();
for (int i = 0; i < 5; i++)
    examples.Add(new LabeledExample<MathQa>(
        Inputs:   new MathQa { Question = $"q{i}" },
        Expected: new MathQa { Answer = "42" }));

var eval = new Evaluate<MathQa>(
    program: (inputs, t) => p.ForwardAsync(inputs, ct: t),
    metric: new ExactMatch<MathQa>(nameof(MathQa.Answer)),
    maxDegreeOfParallelism: 4);

var result = await eval.RunAsync(examples, ct);
result.Scores.Count.ShouldBe(5);
result.Mean.ShouldBe(1.0);
```

`Mean` is the arithmetic mean over per-example scores; `Scores` is the
flat list. `failureSampleCount` retains the lowest-scoring rows for
inspection.

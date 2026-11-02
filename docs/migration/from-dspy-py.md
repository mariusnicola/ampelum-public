# Migrating from DSPy (Python) to Ampelum

Ampelum mirrors the DSPy programming model: signatures, modules
(`Predict`, `ChainOfThought`, `ReAct`), and optimizers (`BootstrapFewShot`,
`MIPROv2`). The translation is mostly mechanical; the surface area diverges
where idiomatic .NET differs from idiomatic Python.

## Signature

**DSPy (Python):**

```python
class Qa(dspy.Signature):
    """Answer the question."""
    question: str = dspy.InputField()
    answer:   str = dspy.OutputField()
```

**Ampelum (.NET):**

```csharp
[Signature("Answer the question.")]
public sealed record Qa : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}
```

The `[Signature]` attribute carries the docstring; `[Input]`/`[Output]` are
the field markers. C# property names PascalCase; the runtime preserves the
exact name in the wire markers (`[[ ## Answer ## ]]`).

## Predict

**DSPy:**

```python
predict = dspy.Predict(Qa)
result  = predict(question="Capital of France?")
print(result.answer)
```

**Ampelum:**

```csharp
var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p   = new Predict<Qa>(ctx);
var r   = await p.ForwardAsync(new Qa { Question = "Capital of France?" });
Console.WriteLine(r.Value.Answer);
```

## ChainOfThought

**DSPy:**

```python
cot = dspy.ChainOfThought(Qa)
r   = cot(question="...")
print(r.rationale, r.answer)
```

**Ampelum:**

```csharp
var cot = new ChainOfThought<Qa>(ctx);
var r   = await cot.ForwardAsync(new Qa { Question = "..." });
Console.WriteLine($"{r.Reasoning()} -> {r.Value.Answer}");
```

DSPy's `rationale` becomes Ampelum's `Reasoning()` accessor.

## ReAct + tools

**DSPy:**

```python
def calc(expr): ...
react = dspy.ReAct(MathSig, tools=[calc])
```

**Ampelum:**

```csharp
public sealed class CalculatorTool : ITypedTool<double> { /* ... */ }
var react = new ReAct<MathSig>(ctx, [new CalculatorTool()], maxIterations: 6);
var r     = await react.ForwardAsync(new MathSig { Question = "..." });
```

## Optimizer

**DSPy:**

```python
optimizer = dspy.BootstrapFewShot(metric=exact_match)
compiled  = optimizer.compile(predict, trainset=examples)
```

**Ampelum:**

```csharp
var optimizer = new BootstrapFewShot(ctx);
var compiled  = await optimizer.CompileAsync(p, trainset, metric, ct: ct);
await JsonProgramPersistence.SaveAsync(compiled, "compiled.json", ct);
```

## What's different

- **Concurrency**: every call is `async Task<...>` and accepts a `CancellationToken`.
- **Audit chain**: every forward call appends a hash-chained `AuditEntry`. There's no Python equivalent.
- **Budgets/hooks**: `BudgetHook` / `RetryHook` / `AuditHook` are pluggable cross-cutting concerns; in DSPy these are typically inline or via the LM client.
- **Type safety**: outputs are parsed into the typed `[Output]` fields. A schema violation throws `AdapterParseException`; the optional `RetryHook` re-prompts.

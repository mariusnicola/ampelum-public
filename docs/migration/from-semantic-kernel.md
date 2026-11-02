# Migrating from Semantic Kernel

Both Semantic Kernel (SK) and Ampelum sit on top of `Microsoft.Extensions.AI`'s
`IChatClient`. The mental shift is from **prompt-template-and-execute** (SK
Functions / `KernelFunctionFromPrompt`) to **declared signature + adapter +
optimizer** (Ampelum's `Predict<TSig>`). Once the signature is declared, the
prompt is generated, the response is parsed into typed fields, and the call
site stays free of string concatenation.

## Single Predict

**Semantic Kernel — `KernelFunctionFromPrompt`:**

```csharp
var kernel = Kernel.CreateBuilder().AddOpenAIChatCompletion(model, key).Build();
var fn = kernel.CreateFunctionFromPrompt(
    "Q: {{$question}}\nA:",
    new OpenAIPromptExecutionSettings { MaxTokens = 256 });
var result = await fn.InvokeAsync(kernel, new() { ["question"] = "Capital of France?" });
string answer = result.GetValue<string>()!;
```

**Ampelum — `Predict<TSig>`:**

```csharp
[Signature("Answer the question.")]
public sealed record Qa : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<Qa>(ctx);
var r = await p.ForwardAsync(new Qa { Question = "Capital of France?" });
string answer = r.Value.Answer;  // typed, parsed, schema-validated
```

## Chain-of-Thought

SK doesn't ship a structured CoT primitive — you concatenate "Let's think step by
step" into your template. Ampelum adds `reasoning` to the schema:

```csharp
var cot = new ChainOfThought<Qa>(ctx);
var r = await cot.ForwardAsync(new Qa { Question = "Capital of France?" });
string answer = r.Value.Answer;
string reasoning = r.Reasoning();
```

## Tools / function calling — ReAct

SK's tool-calling is `KernelFunctionFromMethod` plus
`AutoFunctionInvocationFilter`. Ampelum exposes `[Tool]`-attributed methods to a
`ReAct<TSig>` loop:

```csharp
var tools = new[] { new CalculatorTool() };
var react = new ReAct<MathSig>(ctx, tools, maxIterations: 6);
var r = await react.ForwardAsync(new MathSig { Question = "12 * 31 + 7" });
```

## Optimizer / prompt tuning

SK has no first-class prompt optimizer. Ampelum's `BootstrapFewShot` /
`MIPROv2` optimizers compile a `Predict<TSig>` against a trainset and
metric, persisting the learned demos and instruction:

```csharp
var compiled = await new BootstrapFewShot(ctx).CompileAsync(p, trainset, metric, ct: ct);
```

## Streaming

```csharp
await foreach (var update in p.StreamAsync(new Qa { Question = "..." }))
    Console.Write(update.PartialText);
```

## Things you keep

- `IChatClient` registrations stay intact — Ampelum consumes any MEAI client
- DI lifetime, logging, retry policies (Polly), HttpClientFactory configuration
- Telemetry conventions (`OpenTelemetry.Instrumentation.Http`, etc.)

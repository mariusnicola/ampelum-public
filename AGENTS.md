# Using Ampelum: a guide for coding agents

Ampelum is a .NET 10 library for LLM programs. You declare inputs and outputs as a C# record, and the library builds the prompt, calls an `IChatClient`, and parses the reply into that record. The API is pre-release and can change, so check the code in `src` over anything written from memory.

## Before you rely on a feature

Read [Known limits](README.md#known-limits). Several features are stubs or partial, and some safety features protect less than their names suggest. Do not tell a user a feature works until you have checked it there and run it.

## Write a program

A signature is a record that implements `ISignature`. The `[Signature]` text is the instruction to the model.

```csharp
using SignalGrid.Ampelum;

[Signature("Answer questions with short factoid answers.")]
public sealed record Qa : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

var qa = Predict.Create<Qa>(chat);
var result = await qa.ForwardAsync(new Qa { Question = "Capital of France?" });
Console.WriteLine(result.Value.Answer);
```

`chat` is any `Microsoft.Extensions.AI.IChatClient`. The [README](README.md#quick-look) shows one built for an OpenAI-compatible endpoint.

## Test it without a key

`FakeChatClient` from `SignalGrid.Ampelum.Testing` scripts the model. Use it so tests run offline.

```csharp
var fake = new FakeChatClient().EnqueueOutputs<Qa>(new Qa { Question = "?", Answer = "Paris" });
var predict = Predict.Create<Qa>(fake);
var r = await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
r.Value.Answer.ShouldBe("Paris");
```

A passing fake proves your code path, not the quality of a model's answers. Tests here use xunit v3 and Shouldly.

## Keys and model ids

Never write a key or a model id into code. The library reads environment variables. The samples also read `.env` and `appsettings.json` through `SampleConfig.Load()`. `.sample.env` lists every setting, and `.env` must never be committed.

## Where to look

The README's [Use cases](README.md#use-cases) section lists every sample with its run and test commands. Samples that call a model take `--fake`, which replays a scripted model and needs no key.

- A ticket pipeline with lookups, budget and audit: `samples/Ampelum.Samples.SupportDesk`
- Durable YAML workflow resumed by a new runtime: `samples/Ampelum.Samples.ExpenseApproval`
- Measuring and tuning a prompt: `samples/Ampelum.Samples.PromptTuning`
- Comparing optimizers and exporting traces: `samples/Ampelum.Samples.TracedPipeline`
- Hosting behind HTTP: `samples/Ampelum.Samples.AgentService`
- Safety hooks and what they miss: `samples/Ampelum.Samples.SafetyGateway`
- Composing modules, agents and GEPA tuning: `samples/Ampelum.Samples.ComposedPrograms`
- Planned workflow that stops for approval and resumes: `samples/Ampelum.Samples.AgentRuntime`
- Task-sized examples: `docs/recipes/`
- Concepts and layers: `docs/architecture.md`
- Settings: `docs/configuration.md`

## Build and test

```bash
dotnet build SignalGrid.Ampelum.sln -c Release
dotnet test tests/Ampelum.Tests/Ampelum.Tests.csproj -c Release
```

Warnings are errors, and package versions live in `Directory.Packages.props`. A full-solution test run needs Ollama, PostgreSQL and real credentials for some tests. `.github/workflows/ci.yml` holds the filter that leaves those out.

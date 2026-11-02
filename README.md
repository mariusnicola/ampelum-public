# Ampelum

Declarative LM programming for .NET 10, built on `Microsoft.Extensions.AI.IChatClient`. You write typed C# signatures; Ampelum formats the prompt, calls the model, parses the answer back into your types, and can tune the prompt against your own metric. On top of that sit agents, retrieval, evaluation and a durable workflow runtime.

The packages are pre-release (`0.8.0-rc1`), and the public API can still change. The MIT license is in [LICENSE](LICENSE).

## Why use it

LLM code often turns into prompt strings, hand-written parsing and a prompt nobody wants to touch, because nothing measures whether a change helped. Ampelum brings the DSPy idea to C#: describe what you want in types, then measure and tune it against your own data.

- **Typed programs.** Inputs and outputs are C# records. You get a typed result back, not a string to parse.
- **Prompts you can measure.** Score a program on labeled data, tune it with `MiproV2`, save the result, reload it, and compare before and after in a report with a confidence interval. `MiproV2` keeps a candidate only when it beats the best score so far, starting from the bootstrapped program. The comparison with the untuned prompt is in the report, not the optimizer.
- **Tests that need no key.** `FakeChatClient` scripts the model, so a test suite runs offline and costs nothing.
- **Controls in the call path.** Prompt injection, content policy, budget and audit hooks run around each call. The audit log is a hash chain, and tampering with an entry is detected.
- **Workflows that survive a stop.** A YAML workflow runs on a durable runtime with SQLite persistence, and a new process can resume it without repeating finished steps.
- **No provider lock-in.** It runs on anything behind `IChatClient`.

Each of those is in a sample with a passing test, except the provider point, which follows from the interface. Try one without a key:

```bash
dotnet run --project samples/Ampelum.Samples.SupportDesk -c Release -- --fake
```

`--fake` replays a scripted model, so it shows the code path running, not model quality. Its scores in the prompt tuning sample are scripted too.

On overhead, one short benchmark on a Windows machine with a mock model, run on an earlier build, measured a `Predict` call at 777.65 ns and 4,064 B allocated, against 1,410.08 ns and 5,344 B for a Semantic Kernel prompt call. A bare `IChatClient` call was 22.78 ns and 344 B, so Ampelum does cost more than calling the client directly. The two frameworks do not do identical work in that row, AOT cold start and optimizer speed against DSPy were not measured, and the audit chain costs about 14 times a naive log per call. The numbers and method are in [comparative-results.md](docs/performance/comparative-results.md).

Not everything here is finished. Some features are stubs or partial, and [Known limits](#known-limits) lists them. Read it before you build on one.

## Quick look

```csharp
using System.ClientModel;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using OpenAI;

[Signature("Answer questions with short factoid answers.")]
public sealed record Qa : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

// Any OpenAI-compatible endpoint works. The key and the model come from
// your environment, see .sample.env.
var options = new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") };
IChatClient chat = new OpenAIClient(
        new ApiKeyCredential(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")!), options)
    .GetChatClient(Environment.GetEnvironmentVariable("OPENROUTER_MODEL_1")!)
    .AsIChatClient();

var qa = Predict.Create<Qa>(chat);
var r = await qa.ForwardAsync(new Qa { Question = "Capital of France?" });
Console.WriteLine(r.Value.Answer);
```

The example needs the `OpenAI` and `Microsoft.Extensions.AI.OpenAI` packages, which the core `Ampelum` package does not reference. Any `IChatClient` works.

The library ships no keys and no model ids. `ModelConfig` and `ampelum repl` read environment variables only. The samples also read `.env` and `appsettings.json`. The samples and `ampelum repl` hard-code the OpenRouter endpoint URL. A required model variable that is not set is an error that names the variable. See [docs/configuration.md](docs/configuration.md).

## Use cases

These samples show complete flows. Each runs with `--fake`, which uses a scripted model and needs no key and no network. Without `--fake` they read `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1` (see `.sample.env`).

**Support desk** (`samples/Ampelum.Samples.SupportDesk`). A customer ticket goes through triage, knowledge lookup, order lookup and a checked reply. Budget, audit, prompt-injection and content-policy hooks run on every model call, and the audit chain is verified at the end.

```bash
dotnet run --project samples/Ampelum.Samples.SupportDesk -c Release -- --fake
dotnet test tests/Ampelum.Samples.SupportDesk.Tests/Ampelum.Samples.SupportDesk.Tests.csproj
```

**Expense approval** (`samples/Ampelum.Samples.ExpenseApproval`). A durable workflow defined in YAML. A model extracts the expense, a branch step picks auto-pay or flag-for-review, tool calls go through the policy gate, and the run is stopped and then resumed by a new runtime on the same SQLite database.

```bash
dotnet run --project samples/Ampelum.Samples.ExpenseApproval -c Release -- --fake
dotnet test tests/Ampelum.Samples.ExpenseApproval.Tests
```

**Prompt tuning** (`samples/Ampelum.Samples.PromptTuning`). Measure a prompt on labeled data, optimize it, save and reload the tuned program, and measure again. With `--fake` the scores come from the script, so use it to see the flow, not to judge a prompt.

```bash
dotnet run --project samples/Ampelum.Samples.PromptTuning -c Release -- --fake
dotnet test tests/Ampelum.Samples.PromptTuning.Tests
```

**Traced pipeline** (`samples/Ampelum.Samples.TracedPipeline`). Compare five optimizers on the same labeled rows and export every evaluation trace to LangFuse, LangSmith and Phoenix. Without `--fake` it also needs the tracing settings in `.sample.env`. With `--fake` the scores are scripted and the exporters talk to a recording handler.

```bash
dotnet run --project samples/Ampelum.Samples.TracedPipeline -c Release -- --fake
dotnet test tests/Ampelum.Samples.TracedPipeline.Tests
```

**Bulk documents** (`samples/Ampelum.Samples.BulkDocuments`). Summarize a set of documents through a batch API and retry the ones that fail, keeping what already finished. Interactive requests run through failover, load-balanced or key-rotating routing, with a circuit breaker, a rate limit and an exact-match response cache. Streaming stays on the primary endpoint and does not fail over. Without `--fake` it also needs the keys marked for this sample in `.sample.env`.

```bash
dotnet run --project samples/Ampelum.Samples.BulkDocuments -c Release -- --fake
dotnet test tests/Ampelum.Samples.BulkDocuments.Tests
```

**Research assistant** (`samples/Ampelum.Samples.ResearchAssistant`). A planner, researcher, analyst and writer work through `GroupChat` to answer a question with citations, using hybrid retrieval, reranking and the calculator, Wikipedia, web search and code tools. An answer that cites an unknown source is not grounded. The reranker scorer and the embeddings are stand-ins, and the code tool runs without a sandbox, so its guard allows plain arithmetic only. Without `--fake` it also needs `BRAVE_API_KEY`.

```bash
dotnet run --project samples/Ampelum.Samples.ResearchAssistant -c Release -- --fake
dotnet test tests/Ampelum.Samples.ResearchAssistant.Tests
```

**Vector store swap** (`samples/Ampelum.Samples.VectorStoreSwap`). The same retrieval code runs over pgvector, Pinecone or Qdrant, and one setting picks the store. With `--fake` all three adapters sit on one in-memory fake, so agreement shows the adapters and the code above them, not the databases. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.VectorStoreSwap -c Release -- --fake
dotnet test tests/Ampelum.Samples.VectorStoreSwap.Tests
```

## What is in the repository

| Area | Packages (all prefixed `SignalGrid.`) |
|---|---|
| Core runtime | `Ampelum`, `Ampelum.Testing`, `Ampelum.SourceGenerator`, `Ampelum.Analyzers` |
| Retrieval and storage | `Ampelum.Retrieval`, `Ampelum.Retrieval.Reranking`, `Ampelum.Persistence.Sqlite`, `Ampelum.VectorStores.PgVector`, `Ampelum.VectorStores.Pinecone`, `Ampelum.VectorStores.Qdrant` |
| Agents and workflows | `Ampelum.Agent`, `Ampelum.Agent.Contracts`, `Ampelum.Agent.Tools`, `Ampelum.Agent.Synthesis`, `Ampelum.Agent.Postgres`, `Ampelum.Agent.Yaml` |
| Ready-made tools | `Ampelum.Tools.Calculator`, `Ampelum.Tools.CodeInterpreter`, `Ampelum.Tools.WebSearch`, `Ampelum.Tools.Wikipedia`, `Ampelum.Mcp` |
| Hosting and tooling | `Ampelum.Host`, `Ampelum.Host.Contracts`, `Ampelum.Cli`, `Ampelum.Repl`, `Ampelum.Templates` |
| Observability | `Ampelum.Observability.LangFuse`, `Ampelum.Observability.LangSmith`, `Ampelum.Observability.Phoenix` |

[docs/architecture.md](docs/architecture.md) explains how these fit together.

## Documentation

Start at [docs/README.md](docs/README.md). The short version:

- [Architecture](docs/architecture.md) for the concepts and layers
- [Configuration](docs/configuration.md) for keys, model selection and Host settings
- [Recipes](docs/recipes/) for 20 task-sized examples
- [Migration guides](docs/migration/) if you are coming from another framework
- [Threat model](docs/security/threat-model-v1.md) for the security position
- [Known limits](#known-limits) below for what is partial or unfinished

## Build and test

You need the .NET SDK pinned in [global.json](global.json).

```bash
dotnet build SignalGrid.Ampelum.sln -c Release
dotnet test tests/Ampelum.Tests/Ampelum.Tests.csproj -c Release
```

`tests/Ampelum.ContractTests` includes live Ollama tests, and these fail rather than skip when Ollama is not running. Some Host and provider tests also need real credentials, so a full-solution run without them will not be green. `tests/ConsumerSmoke.Tests` sits outside the solution on purpose and needs locally packed packages: see [docs/release/consumer-smoke.md](docs/release/consumer-smoke.md).

CI is configured in [.github/workflows/ci.yml](.github/workflows/ci.yml). It builds the solution and runs the unit tests on Linux. It leaves out the Ollama tests, the PostgreSQL tests and the analyzer packaging test, which need a running service or a packed nupkg.

## Known limits

These are partial or unfinished parts of the current code. Each one was checked against the source.

Safety and resilience

- PII redaction reaches the audit log only. `PiiRedactionHook` publishes a redacted copy of the inputs in hook metadata and redacts the outputs in its after step, and `AuditHook` writes those. The prompt sent to the model, the value returned to the caller and the trace record are built from the unredacted text (`src/Ampelum/Safety/Pii/PiiRedactionHook.cs`, `src/Ampelum/Predict.cs`).
- `RetryHook` computes an exponential backoff with jitter and stores it in hook metadata as `retry.next_delay_ms`. Nothing waits on it (`src/Ampelum/Hooks/RetryHook.cs`, lines 80 to 87).

Optimizers

- `BootstrapFewShot` accepts a metric but never calls it. Every successful trace becomes a demo candidate, whether or not it scores well (`src/Ampelum/Optimizers/BootstrapFewShot.cs`, lines 41 to 48).
- `Ensemble` does not optimize anything. It wraps the programs you pass and a reducer you supply into an `EnsembleProgram` (`src/Ampelum/Optimizers/Ensemble.cs`).

Stubs and placeholders

- `EditTool.Execute` returns a string and edits no file (`src/Ampelum.Agent.Tools/EditTool.cs`).
- `EntraIdentityProvider` issues a placeholder token of the form `entra:<tenant>:<instance>` and accepts any token of that form. It does not call Entra or check a signature (`src/Ampelum.Agent/Identity/EntraIdentityProvider.cs`).
- The default cross-encoder scorer, `OnnxScorer`, feeds the model position indexes sized from the string lengths instead of tokenized text, so its scores are not meaningful. `CrossEncoderReranker` is only useful with your own `IRelevanceScorer` (`src/Ampelum.Retrieval.Reranking/CrossEncoderReranker.cs`, from line 82).
- `WorkflowStateMachine.CheckpointAsync` does nothing (`src/Ampelum.Agent/Orchestration/WorkflowStateMachine.cs`, line 137).
- `EmbeddingEngine` falls back to a hash-based vector, without a log message, when no ONNX model loads (`src/Ampelum.Agent/CodeIntelligence/EmbeddingEngine.cs`, lines 75 to 92).

Workflows

- The `WaitForSignal`, `Evaluate`, `EmitEvent` and `Compensate` step kinds are accepted by `WorkflowYamlCompiler`, but `DurableWorkflowRuntime` rejects a definition that contains one, with an `ArgumentException` (`src/Ampelum.Agent/Orchestration/DurableWorkflowRuntime.cs`: the set is at lines 591 to 597, the throw at lines 618 and 619).
- `Approval` and `Wait` steps are not rejected, but nothing in the runtime or the compiler gives them pause behavior. They run like any other step through your handler, and `WorkflowToolStepHandler` fails any step that is not a `Tool` step (`src/Ampelum.Agent/Orchestration/WorkflowToolStepHandler.cs`, line 62).
- A `WorkflowPolicyGate` decision of `RequireConfirmation` fails the step. There is no approve-then-resume path (same file, line 68).
- `DefaultWorkflowSynthesizer.PromoteAsync` validates and writes a candidate. It does not call `WorkflowPromotionEvaluator`, and nothing in the repository produces the evaluations that class expects (`src/Ampelum.Agent.Synthesis/DefaultWorkflowSynthesizer.cs`, lines 115 to 136).

MCP

- `HttpMcpTransport` calls `tools/list` once and does not follow the next-cursor field, so a server that paginates returns only its first page (`src/Ampelum.Agent/Mcp/McpModels.cs`, lines 111 to 127).

Host

- `rebuild-index --config <path>` ignores the path. `Program` passes the whole argument list, so the first argument is `rebuild-index` and the `--config` check never matches; the command always reads `appsettings.json` from the working directory (`src/SignalGrid.Ampelum.Host/Program.cs`, line 17, and `src/SignalGrid.Ampelum.Host/Tools/HostCli.cs`, line 20).

Written but not called by shipped code

- `ConfiguredModelRouter` with `ModelRoutingTable`, `WorkflowPromotionEvaluator`, `DurableAgentRuntime`, `CronPortfolioScheduler`, `IExecutionSandbox` with `ProcessSandbox`, `NullOptimizerRunner` in the Host, and the Host option `Host:Modules:RegistryFile`, which the code marks as reserved. They are public API and most have tests, but no other code in this repository uses them.

Templates

- The `ampelum-enterprise` template does not compile against this code. It calls `AddAmpelumAudit` and `AddAmpelumBudget` and uses `Ampelum.Audit.AuditOptions` and `Ampelum.Budget.BudgetOptions`. None of these exist.
- The `ampelum-researcher` and `ampelum-backend` templates call `Predict.RunAsync`, and the researcher also calls `Predict.Create<T>()` with no client and `AmpelumContext.ForTests`. The real API is `Predict.Create<T>(chat)` and `ForwardAsync`, which returns a prediction whose `Value` holds the typed result. The `ampelum-agent` template imports `SignalGrid.Ampelum.Agents.Selection`, which is `SignalGrid.Ampelum.Agents.SpeakerSelectors` in the code.
- The template projects reference a package named `Ampelum` at `0.9.0-alpha.6`. The package id in this repository is `SignalGrid.Ampelum`, at `0.8.0-rc1`, so those references do not match the packages this code produces.
- The template tests read the template text and project files. They do not build a generated project, so they do not catch this drift.

## Security

Never commit keys. Copy `.sample.env` to `.env` for local work and fill in the values you need: `.env` and `appsettings*.json` are ignored by git. `.sample.env` only names the variables and explains each one, and holds no values. If you find a vulnerability, please report it privately through GitHub security advisories on this repository rather than in a public issue.

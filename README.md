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

**Expense approval** (`samples/Ampelum.Samples.ExpenseApproval`). A durable workflow defined in YAML. A model extracts the expense, a branch step picks auto-pay or flag-for-review, tool calls go through the policy gate, and the run is stopped and then resumed by a new runtime from the stored checkpoint, so the finished extraction is not repeated. State lives in a SQLite file by default, or in PostgreSQL through `PostgresWorkflowPersistence` with `--postgres`; the PostgreSQL tests need a database and are left out of CI.

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

**Agent service** (`samples/Ampelum.Samples.AgentService`). A program written once is served over HTTP by the Ampelum Host. The sample calls and streams it, reads and verifies its audit trail, looks entries up by trace id, replays a recorded call without the model, stores and promotes prompt versions, starts optimizer runs and follows their progress over SSE, and rebuilds the Host's index with the admin endpoint and with the Host's own command line. Its order tool is served as an MCP server on loopback, through a small transport the sample writes, and consumed back by a direct `tools/call` and by a ReAct agent. The CLI scores answers with `eval`, scaffolds a project with `new` and refuses to start `repl` without a key. Several of these Host features are plumbing with defects, and the sample's tests pin each one.

```bash
dotnet run --project samples/Ampelum.Samples.AgentService -c Release -- --fake
dotnet test tests/Ampelum.Samples.AgentService.Tests
```

**Build-time tools** (`samples/Ampelum.Samples.BuildTimeTools`). The source generator and the ten analyzers working on a small project: generated signature metadata and accessors at runtime, and each rule with a snippet that fires and one that stays quiet. It needs no model and no key. Two rules are recorded with their flaws, and the sample README says which.

```bash
dotnet run --project samples/Ampelum.Samples.BuildTimeTools -c Release
dotnet test tests/Ampelum.Samples.BuildTimeTools.Tests
```

**Safety gateway** (`samples/Ampelum.Samples.SafetyGateway`). An internal assistant whose calls pass PII redaction, moderation, a tool gate, a sanitizer for secrets, an audit trail that can be verified and replayed, usage and cost tracking, and a secret store. It shows limits as well as protections: redaction reaches the audit file only, and the sanitizer catches known key shapes, so its README lists what passes. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.SafetyGateway -c Release -- --fake
dotnet test tests/Ampelum.Samples.SafetyGateway.Tests
```

**Composed programs** (`samples/Ampelum.Samples.ComposedPrograms`). Release notes drafted from a CSV of changes. Each change is classified, drafted by a strategy chosen by its kind and impact, tightened in a loop and scored. A panel of agents then reviews the notes, ReAct checks the facts, a task agent writes the announcement, and the program can be measured and tuned with GEPA against the previous release's notes. Under `--fake` the tuning figures are scripted, and they are measured on the notes GEPA trained on. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.ComposedPrograms -c Release -- --fake
dotnet test tests/Ampelum.Samples.ComposedPrograms.Tests
```

**Agent runtime** (`samples/Ampelum.Samples.AgentRuntime`). A long task over a small demo project: triage three backlog issues, edit the report, stop for approval, then post. The model plans a workflow in YAML that uses fan out, join, loop, approval, wait and tool steps. The run pauses at the approval step and a second runtime, with new objects on the same state folder, resumes it without repeating a step or a model call. Along the way it shows the event log, memory that keeps the goal across the restart, skill selection, model tiers, a budget that defers issues, a tool that needs a grant, conventions and code facts. Both runtimes live in one OS process, and the pause comes from the sample's approval handler because the runtime gives `Approval` no pause of its own. After the report is posted, a coding sub-agent built on `SubAgentRunner` fixes one issue in the temp copy of the project with the runner's file tools and ends on `DONE:`, and the sample reads the edited file back instead of trusting the claim. The runner has no shell, but its file tools are not confined to the working directory; the tests pin each escape. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.AgentRuntime -c Release -- --fake
dotnet test tests/Ampelum.Samples.AgentRuntime.Tests
```

**Chat frontend** (`samples/Ampelum.Samples.ChatFrontend`). The backend of a customer chat widget. A conversation reply streams token by token through `StreamingChatSession`, and the typed fields of an answer stream through `ProgramStreamer`. A repeated question is answered by the semantic cache hook without a model call, a repeated identical prompt is stopped by `TokenCache`, and `RoutedChatClient` with `Failover` sends a call to the fallback model when the primary fails. Its tests pin what does not work: streaming does not fail over, moderation never sees a streamed reply, a cache hit skips the PII hook, and the "semantic" cache matches identical inputs only. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.ChatFrontend -c Release -- --fake
dotnet test tests/Ampelum.Samples.ChatFrontend.Tests
```

**Document review** (`samples/Ampelum.Samples.DocumentReview`). A team that receives supplier contracts reads the PDFs with `PdfLoader`, ranks the clauses that matter for a question with the Cohere or Voyage reranker, and sends the ones the ranking is unsure about to a remote reviewer agent through `A2AClient`. The same run queues bulk overnight summaries through `OpenAIBatchSubmitter` and cancels a batch when a document is withdrawn. Under `--fake` one scripted HTTP handler per service plays the vendor, written from the library's own tests rather than vendor docs. The README lists the defects found on the way, among them that the A2A card verifier checks no capabilities and accepts a token issued to another agent. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.DocumentReview -c Release -- --fake
dotnet test tests/Ampelum.Samples.DocumentReview.Tests
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
- `DurableAgentRuntime.PauseAsync` followed by `ResumeAsync` on the same instance throws "Cannot resolve escalation from state Running". A fresh instance on the same state store resumes.
- `SessionBudget` reports `IsExhausted` and `IsWarning`, but nothing in `src` enforces the budget.
- `SkillRegistry.LoadAsync` does nothing.
- `JsonFileProjectRegistry` stores label, root and escalation policy only; the blast radius and allowed tools are derived again on load.
- The code indexer's `ExtractCalls` covers only Method or Function symbols, and its method pattern misses plain return types, so in practice the index holds type declarations and imports.
- `ISynthesisInfrastructure` has no implementation in the library.
- `SubAgentRunner` file tools accept absolute paths and `..` without a check, so they read and write outside the working directory. The runner does not run tool calls itself (wrap the client in `FunctionInvokingChatClient`), its prompt names `edit_file`, `build` and `run_tests`, which do not exist, a failed write still appears in `FilesChanged`, and `SubAgentTask.Tier` is ignored (`src/Ampelum.Agent/Execution/SubAgentRunner.cs`).
- `PostgresWorkflowPersistence` has no API to delete an execution's rows, and its constructor runs schema DDL on every instance, so it needs DDL rights (`src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs`, line 31).
- `DefaultWorkflowSynthesizer.PromoteAsync` validates and writes a candidate. It does not call `WorkflowPromotionEvaluator`, and nothing in the repository produces the evaluations that class expects (`src/Ampelum.Agent.Synthesis/DefaultWorkflowSynthesizer.cs`, lines 115 to 136).

MCP

- `HttpMcpTransport` calls `tools/list` once and does not follow the next-cursor field, so a server that paginates returns only its first page (`src/Ampelum.Agent/Mcp/McpModels.cs`, lines 111 to 127).
- `McpServerAdapter` has no transport in `src`, so Ampelum can call MCP servers but cannot be served as one without your own transport. The agent service sample writes a minimal JSON-RPC over HTTP one.

A2A, loaders and batch

- `DefaultAgentCardVerifier.VerifyCapabilities` always returns no warnings, and `VerifyIdentityAsync` checks only signature and expiry, so a valid token issued to another agent is accepted (`src/Ampelum.Agent/A2A/A2AIntegration.cs`).
- `A2AClient` does not check that the card's URL belongs to the host it came from, and `Dispose` disposes the `HttpClient` it was given (`src/Ampelum.Agent/A2A/A2AClient.cs`).
- `PdfLoader` yields zero pages and no error for a corrupt file or a non-PDF (`src/Ampelum.Retrieval/Loaders/PdfLoader.cs`).
- `OpenAIBatchSubmitter` maps the `cancelling` state to `Cancelled`, and `StreamResultsAsync` throws when the batch has no output file.

Host

- `rebuild-index --config <path>` ignores the path. `Program` passes the whole argument list, so the first argument is `rebuild-index` and the `--config` check never matches; the command always reads `appsettings.json` from the working directory (`src/SignalGrid.Ampelum.Host/Program.cs`, line 17, and `src/SignalGrid.Ampelum.Host/Tools/HostCli.cs`, line 20).

- The optimize progress stream keeps no history. `OptimizerProgressBus.Publish` drops an event when no client is subscribed yet, so a client that connects after a run has started misses the earlier progress (`src/SignalGrid.Ampelum.Host/Optimize/OptimizerProgressBus.cs`).
- `ProblemDetailsMiddleware` is internal and nothing registers it, so an unhandled endpoint exception is not mapped to a Host error code.
- The Host optimize endpoint scores with a private `NoOpMetric` that returns 1.0, so it does not measure quality (`src/SignalGrid.Ampelum.Host/ModuleRegistry/PredictComposedAdapter.cs`).
- A promoted prompt version is never applied to forward, prompt demo examples read back with null fields, and `rebuild-index` drops prompt versions created through the endpoint.
- Audit lookup by trace id returns 404 until the index is rebuilt. Replay looks the module up by the audited name `Predict`, so a module registered under another name cannot be replayed.
- `/audit/verify` and replay reject entries read back by `JsonFileAuditSink`, because it returns `JsonElement` values the hasher refuses.
- The Host `rebuild-index` command fails when the working directory holds an `appsettings.json`.
- `/readyz` answers 503 when one SQLite open takes longer than 50 ms, which can happen on a cold start, so a client must poll it (`src/SignalGrid.Ampelum.Host/Persistence/HostSqliteConnectionFactory.cs`, line 39).
- `ChatAdapter.OnChunk` treats the closing `[[ ## completed ## ]]` marker as a field. The Host stream endpoint filters it out, but a direct `Predict.StreamAsync` caller sees an update named `completed`.
- A chunk that carries the last tokens and that marker together is not sent as a `partial`, so the last `partial` can lag the text. The `final` frame is always complete.


Written but not called by shipped code

- `WorkflowPromotionEvaluator`, `CronPortfolioScheduler`, `NullOptimizerRunner` in the Host, and the Host option `Host:Modules:RegistryFile`, which the code marks as reserved. They are public API and most have tests, but no other code in this repository uses them.
- `ConfiguredModelRouter` with `ModelRoutingTable` and `DurableAgentRuntime` are used only by the agent runtime sample, and `IExecutionSandbox` with `ProcessSandbox` only by the safety gateway sample.

- `PhoenixCredentials` is accepted by `AddPhoenixExporter` and nothing reads it. Delivery and its API key belong to the OTLP exporter the host configures.

Audit and safety

- `JsonFileAuditSink` returns `JsonElement` values that `AuditChainVerifier` rejects, so a file cannot be verified or replayed through its own sink.
- `AuditHook` cannot continue an existing chain. It starts from the zero hash, so a second run on the same file breaks at sequence 1.
- Replay of a question `PiiRedactionHook` changed fails with `AuditReplayMissingException`: entries are written from redacted inputs and the key is built from the original ones.
- `AuditHook` records model id `unknown` and 0 tokens for `Predict` calls, because it reads a different metadata key from the one they set.
- `SemanticCacheHook` stops the before-pass on a hit, so `PiiRedactionHook` never runs and the audit entry holds the original text.
- `StreamingChatSession` sends text to the caller before the after hooks run and never sets outputs, so `ModerationHook` does not check streamed replies.
- `BatchedAuditSink` does not dispose the sink it wraps, and `AddBatchedAuditSink<T>` cannot wrap `JsonFileAuditSink`.
- `FileTrustStore` keeps everything in memory, despite its name, and violations do not revoke trust.
- Nothing in `src` sets `tools.requested`, so `ToolGateHook` does nothing unless the caller sets it.
- `ModelRateTable.EstimateCost` ignores its model id.

Caching

- `SemanticCacheHook` is an exact match on a SHA-256 of the module name and the inputs; it uses no embeddings, so a reworded question misses (`src/Ampelum/Caching/Semantic/SemanticCacheKey.cs`).
- The semantic cache key ignores the signature type, because `Predict` names every module `Predict`, so two signatures with the same input names share entries (`src/Ampelum/Predict.cs`).
- Nothing in `src` calls `TokenCache`, and its key holds only the model id and the messages, so calls that differ in temperature or other options share an entry.

Routing and tracing

- Streaming does not fail over. `RoutedChatClient` streams from the primary decision only (`src/Ampelum/Routing/RoutedChatClient.cs`).
- The `AddLangFuseExporter`, `AddLangSmithExporter` and `AddPhoenixExporter` calls wrap the prior `ITraceSink`, and the exporter never disposes it, so a batched inner sink is not flushed by disposing the exporter.

Retrieval and vector stores

- `HybridRetriever` passes a fixed `k: 60` to reciprocal rank fusion, so a configured value has no effect (`src/Ampelum/Retrieval/Hybrid/HybridRetriever.cs`).
- The pgvector backend interpolates the table name and the filter key into SQL. Filter values are parameters, so treat table names and filter keys as trusted input.
- Qdrant replaces a non-UUID id with an MD5-derived UUID and returns that, so the original id is lost. Non-canonical UUID forms come back canonical.
- `InMemoryVectorStore` returns documents with their indexed score, so scores read 0 through `EmbeddingRetriever`. pgvector returns metadata as `JsonElement`, the other adapters as strings.
- `PineconeOptions.Environment` is required and unused.

Tools

- `IAgentTool` has no argument path. `InvokeAsync` takes only a cancellation token, so arguments must already be set on the tool instance.

Build-time tools

- AMP012 is inverted under the default order: it flags a controller calling a service, the normal direction.
- The generator's per-signature output uses unqualified type names, so a signature in a namespace whose first segment is `Ampelum` fails to build with CS0234.
- AMP013's message says `'member'` for local variables.
- `tests/Ampelum.Analyzers.Tests/PackagingTests` needs a prior `dotnet pack`, so CI filters it out.


Templates

- The `ampelum-enterprise` template does not compile against this code. It calls `AddAmpelumAudit` and `AddAmpelumBudget` and uses `Ampelum.Audit.AuditOptions` and `Ampelum.Budget.BudgetOptions`. None of these exist.
- The `ampelum-researcher` and `ampelum-backend` templates call `Predict.RunAsync`, and the researcher also calls `Predict.Create<T>()` with no client and `AmpelumContext.ForTests`. The real API is `Predict.Create<T>(chat)` and `ForwardAsync`, which returns a prediction whose `Value` holds the typed result. The `ampelum-agent` template imports `SignalGrid.Ampelum.Agents.Selection`, which is `SignalGrid.Ampelum.Agents.SpeakerSelectors` in the code.
- The template projects reference a package named `Ampelum` at `0.9.0-alpha.6`. The package id in this repository is `SignalGrid.Ampelum`, at `0.8.0-rc1`, so those references do not match the packages this code produces.
- The template tests read the template text and project files. They do not build a generated project, so they do not catch this drift.

## Security

Never commit keys. Copy `.sample.env` to `.env` for local work and fill in the values you need: `.env` and `appsettings*.json` are ignored by git. `.sample.env` only names the variables and explains each one, and holds no values. If you find a vulnerability, please report it privately through GitHub security advisories on this repository rather than in a public issue.

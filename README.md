# Ampelum

Declarative LM programming for .NET 10, built on `Microsoft.Extensions.AI.IChatClient`. You write typed C# signatures; Ampelum formats the prompt, calls the model, parses the answer back into your types, and can tune the prompt against your own metric. On top of that sit agents, retrieval, evaluation and a durable workflow runtime.

The packages are pre-release (`0.8.0-rc1`), and the public API can still change. The MIT license is in [LICENSE](LICENSE).

## Why use it

LLM code often turns into prompt strings, hand-written parsing and a prompt nobody wants to touch, because nothing measures whether a change helped. Ampelum brings the DSPy idea to C#: describe what you want in types, then measure and tune it against your own data.

- **Typed programs.** Inputs and outputs are C# records. You get a typed result back, not a string to parse.
- **Prompts you can measure.** Score a program on labeled data, tune it with `MiproV2`, save the result, reload it, and compare before and after in a report with a confidence interval. `MiproV2` builds bootstrapped demo sets and model-proposed instructions for each submodule, searches their combinations with a seeded Tree-structured Parzen Estimator on random minibatches, and returns the combination with the best score on the whole validation set. The current instruction and the zero-shot set are always candidates. The comparison with the untuned prompt is in the report, not the optimizer. `SimbaOptimizer` grows a pool of candidates from the rows a program is unstable on, adding a demo or a model-written rule to the winning run's program. `BootstrapFinetune` hands the runs that pass the metric to a fine-tuning provider you supply (`IFineTuneProvider`) and binds the tuned client to its submodule. `BetterTogether` runs prompt and weight optimizers in turn (default `p -> w -> p`), scores each step on a validation set and returns the best one.
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

**Expense approval** (`samples/Ampelum.Samples.ExpenseApproval`). A durable workflow defined in YAML. A model extracts the expense, a branch step picks auto-pay or flag-for-review, tool calls go through the policy gate, and the run is stopped and then resumed by a new runtime from the stored checkpoint, so the finished extraction is not repeated. After payment the workflow waits for a receipt signal (`WaitForSignal`), checks the receipt against the extracted amount (`Evaluate`), announces the settlement (`EmitEvent`) and, when the receipt does not match, refunds the payment (`Compensate`). State lives in a SQLite file by default, or in PostgreSQL through `PostgresWorkflowPersistence` with `--postgres`; the PostgreSQL tests need a database and are left out of CI.

```bash
dotnet run --project samples/Ampelum.Samples.ExpenseApproval -c Release -- --fake
dotnet test tests/Ampelum.Samples.ExpenseApproval.Tests
```

**Prompt tuning** (`samples/Ampelum.Samples.PromptTuning`). Measure a prompt on labeled data, optimize it, save and reload the tuned program, and measure again. With `--fake` the scores come from the script, so use it to see the flow, not to judge a prompt. Under `--fake` it also runs `SimbaOptimizer`, `BootstrapFinetune` and `BetterTogether` with scripted clients and a scripted fine-tuning provider.

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

**Agent service** (`samples/Ampelum.Samples.AgentService`). A program written once is served over HTTP by the Ampelum Host. The sample calls and streams it, reads and verifies its audit trail, looks entries up by trace id, replays a recorded call without the model, stores and promotes prompt versions, starts optimizer runs and follows their progress over SSE, and rebuilds the Host's index with the admin endpoint and with the Host's own command line. Its order tool is served as an MCP server on loopback, through a small transport the sample writes, and consumed back through `SdkMcpTransport` by a direct `tools/call` and by a ReAct agent; the governed path is shown over HTTP and over stdio, where the sample starts itself as the child-process server and a catastrophic or unannotated tool is blocked before the server gets the call. The CLI scores answers with `eval`, scaffolds a project with `new` and refuses to start `repl` without a key. Several of these Host features are plumbing with defects, and the sample's tests pin each one.

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

**Agent runtime** (`samples/Ampelum.Samples.AgentRuntime`). A long task over a small demo project: triage three backlog issues, edit the report, stop for approval, then post. The model plans a workflow in YAML that uses fan out, join, loop, approval, wait and tool steps. The run pauses at the approval step and a second runtime, with new objects on the same state folder, resumes it without repeating a step or a model call. Along the way it shows the event log, memory that keeps the goal across the restart, skill selection, model tiers, a budget that defers issues, a tool that needs a grant, conventions and code facts. Both runtimes live in one OS process. The pause is the runtime's own: the approval step parks the execution, and the second runtime delivers the decision with `DecideAsync` and resumes. After the report is posted, a coding sub-agent built on `SubAgentRunner` fixes one issue in the temp copy of the project with the runner's file tools and ends on `DONE:`, and the sample reads the edited file back instead of trusting the claim. The runner has no shell, but its file tools are not confined to the working directory; the tests pin each escape. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.AgentRuntime -c Release -- --fake
dotnet test tests/Ampelum.Samples.AgentRuntime.Tests
```

**Chat frontend** (`samples/Ampelum.Samples.ChatFrontend`). The backend of a customer chat widget. A conversation reply streams token by token through `StreamingChatSession`, and the typed fields of an answer stream through `ProgramStreamer`. A repeated question is answered by the semantic cache hook without a model call, a repeated identical prompt is stopped by `TokenCache`, and `RoutedChatClient` with `Failover` sends a call to the fallback model when the primary fails. Its tests pin what does not work: streaming does not fail over, moderation checks a streamed reply only after its text was delivered, and the "semantic" cache matches identical inputs only. The live path has never been run.

```bash
dotnet run --project samples/Ampelum.Samples.ChatFrontend -c Release -- --fake
dotnet test tests/Ampelum.Samples.ChatFrontend.Tests
```

**Document review** (`samples/Ampelum.Samples.DocumentReview`). A team that receives supplier contracts reads the PDFs with `PdfLoader`, ranks the clauses that matter for a question with the Cohere or Voyage reranker, and sends the ones the ranking is unsure about to a remote reviewer agent through `A2AClient`. The same run queues bulk overnight summaries through `OpenAIBatchSubmitter` and cancels a batch when a document is withdrawn. Under `--fake` one scripted HTTP handler per service plays the vendor, written from the library's own tests rather than vendor docs. The README lists the defects found on the way, among them that the A2A client wrote role and state only as numbers and required timestamps in a task. The live path has never been run.

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

## MCP

There are two MCP packages and they do different jobs. `Ampelum.Mcp` turns the tools of an MCP server into `AIFunction`s with no governance: use it when you trust the server and only want its tools in a chat client. `Ampelum.Agent` is the governed path: use it when a tool call must pass policy, danger tier, confirmation and audit. `McpToolSource` lists a server's tools, `SdkMcpTransport` (built on the official `ModelContextProtocol.Core` client) talks to the server over stdio or HTTP, and `McpToolSource.RegisterHandlersAsync` makes the tools callable through `ToolDispatcher`, which decides before anything goes on the wire.

```csharp
await using var transport = SdkMcpTransport.CreateStdio(new McpStdioConnection("my-server", ["--stdio"]));
// or: SdkMcpTransport.CreateHttp(new McpServerConnection("http://127.0.0.1:8080/mcp", TrustLevel.Unknown, apiKey));
var source = new McpToolSource(new McpServerConnection("stdio://my-server", TrustLevel.Unknown), transport, verifier);
var dispatcher = new ToolDispatcher(new ToolLoader([source]), new InMemoryTokenStore(), new PolicyEngine(), caller);
await source.RegisterHandlersAsync(dispatcher);
var result = await dispatcher.InvokeAsync("mcp/my-server/lookup_order", args, caller);
```

Without a verifier every discovered tool is capped to the most dangerous tier, so the dispatcher blocks it; a verifier that accepts a tool lets its declared `_meta.dangerTier` stand. Disposing the transport ends a stdio child process. `HttpMcpTransport` is superseded by `SdkMcpTransport` and stays only because it is public API.

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

- `BootstrapFewShot` scores each run once, on the outputs of the run's last trace against the example's expected outputs. A run at or above `metricThreshold` (default 1.0, for a metric in [0, 1]) keeps the traces of all its submodules as demos; a run below it keeps none. `MiproV2` and `BootstrapFewShotWithRandomSearch` take the same `metricThreshold` and pass it on. A program passed to it must be bound to a context, or its runs fail and no demo is bootstrapped.
- `BootstrapFinetune` has no provider: the library ships no `IFineTuneProvider`, so you write the adapter for the service you use, and `BetterTogether` needs one too unless you pass your own weight optimizer. The tuned client is not saved by `JsonProgramPersistence`. A training example is the messages the module sent in that call, instruction override and demos included, followed by its response.
- `SimbaOptimizer` builds a demo from the fields of the whole program (the example's inputs and the best run's outputs), so on a program with several submodules a demo fits only a submodule with those fields; a candidate with a demo that does not fit scores badly and drops out. It needs run to run variation to find anything: a deterministic model gives every example a spread of 0 and the result is the unchanged program.
- `BetterTogether` merges what its steps produce per submodule, and scores each merged result itself. `OptimizedProgram.Bind` keeps the client, demos and instruction override that an earlier `Bind` set on a module, unless the program being bound sets that field, so a prompt step after a weight step searches with the tuned client and the earlier instructions. A module you bound yourself with `WithContext` is not touched by this: it gets the client of the context given to `Bind`, as before. The carry-over works for `Predict` and `ChainOfThought` submodules only; any other submodule, a nested composite included, is bound as before and loses the earlier steps' work on a later `Bind`.
- `Ensemble` does not optimize anything. It wraps the programs you pass and a reducer you supply into an `EnsembleProgram` (`src/Ampelum/Optimizers/Ensemble.cs`).
- `GEPAOptimizer` tunes instructions only; it learns no demos. A merge needs two frontier candidates that changed different submodules, so a program with one submodule never merges. The result is a set of instruction overrides to bind with `OptimizedProgram.Bind`; for a program that is not an `IComposedModule` (a bare `Predict`) they are stored under the name `__single__`, and `Bind` gives the module that name so it reads them. `MaxMetricCalls` is a hard cap: no run starts past it, and a candidate whose evaluation it cuts short is dropped, so the result is the best fully evaluated candidate. A cap smaller than one run of the validation set is rejected.

Stubs and placeholders

- `EditTool.Execute` returns a string and edits no file (`src/Ampelum.Agent.Tools/EditTool.cs`).
- `EntraIdentityProvider` issues a placeholder token of the form `entra:<tenant>:<instance>` and accepts any token of that form. It does not call Entra or check a signature (`src/Ampelum.Agent/Identity/EntraIdentityProvider.cs`).
- The default cross-encoder scorer, `OnnxScorer`, feeds the model position indexes sized from the string lengths instead of tokenized text, so its scores are not meaningful. `CrossEncoderReranker` is only useful with your own `IRelevanceScorer` (`src/Ampelum.Retrieval.Reranking/CrossEncoderReranker.cs`, from line 82).
- `WorkflowStateMachine.CheckpointAsync` does nothing (`src/Ampelum.Agent/Orchestration/WorkflowStateMachine.cs`, line 137).
- `EmbeddingEngine` falls back to a hash-based vector, without a log message, when no ONNX model loads (`src/Ampelum.Agent/CodeIntelligence/EmbeddingEngine.cs`, lines 75 to 92).

Workflows

- `WorkflowYamlCompiler` cannot declare a metric threshold or an evaluation plan (`src/Ampelum.Agent.Yaml/WorkflowYamlCompiler.cs`, line 311 sets `EvaluationPlan: null`), so an `Evaluate` step in a YAML workflow is rejected by `DurableWorkflowRuntime` until the caller adds the plan and the thresholds to the compiled definition, as the expense approval sample does.
- Exactly-once audit events across a crash need a transactional store. The `event.<name>` line of an `EmitEvent` step and the `node.evaluated` line of an `Evaluate` step (every observed metric with its value and threshold result) are written by the same write that settles the node. `SqliteWorkflowPersistence` and `PostgresWorkflowPersistence` commit the two together. `InMemoryWorkflowStateStore` with a separate audit sink writes the node, then the line, so a crash between the two loses the line (the node stays done and the line is not written again).
- A rejected `Approval` step does not compensate inside `DecideAsync`. When completed steps still owe compensations the execution stays `Running` with the failure reason set, and `ResumeAsync` compensates, then fails it. When several steps finish in one batch and one fails, the others are recorded first, so a succeeded sibling is compensated; `CompletionOrder` inside one batch is the order of the workflow's step list.
- `Wait` steps have no pause of their own; they run like any other step through your handler. `Approval` steps pause the execution (`AwaitingConfirmation`) until `DecideAsync` approves or rejects them, and a `RequireConfirmation` policy decision in `WorkflowToolStepHandler` pauses the step the same way. `WorkflowToolStepHandler` fails any step that is not a `Tool` step (`src/Ampelum.Agent/Orchestration/WorkflowToolStepHandler.cs`, line 61).
- After a rejection the checkpoint is `Failed` at once, but the `execution.failed` audit line is appended in a separate write; a crash between the two loses only that audit line.
- `SessionBudget` is enforced only where a `SessionBudgetMeter` is passed in: `ToolDispatcher` (one call per tool run that reaches its handler, plus the bytes of its output; a call refused for its observation token is given back and does not count, and a call refused for budget does not consume the token) and `SubAgentRunner` (one call per run, plus the tokens of each turn; between turns only the token, output-byte and duration limits stop a run, never the call count, so the run that took the last slot may finish). Nothing else counts against it, and `IsWarning` is not used.
- `SkillRegistry.LoadAsync` reads only `*.json` files directly in the folder (not YAML, not `SKILL.md`, not subfolders), as one skill object or an array per file. A skill without a name or a version (missing, null or blank, or a `null` entry) makes the load throw `JsonException` naming the file, and nothing from any file is registered.
- `JsonFileProjectRegistry` stores label, root, weight, escalation policy, blast radius and allowed tool ids. A file written by an older version has no blast radius or tool ids and loads with empty ones. The token store, secret store and skills are session-local and are not stored.
- The code indexer is regex based. It finds methods whose return type, name and parameter list sit on one line, and records the calls inside their bodies; a signature that spans several lines is missed, and type declarations are reported at the line before them. Methods with a tuple return type, operator overloads and event members are not indexed, and constructors are indexed as methods. A single line made of many `<...>` tokens without parentheses is slow to scan (about 1.1 s for 50,000 characters of `A<B> ` on one test machine, growing much faster than the line length).
- `SubAgentRunner` refuses a file path that resolves outside its working directory by its text, so a symlink or junction inside the working directory that points out of it is followed. Its tool parameters have no defaults (a call that leaves out an optional one fails), and the prompt's "Working directory:" line prints the task id (`src/Ampelum.Agent/Execution/SubAgentRunner.cs`).
- `PostgresWorkflowPersistence` has no API to delete an execution's rows. Its constructor checks the schema on every instance and runs DDL only when part of it is missing, so the first instance against an empty database needs DDL rights (`src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs`, `EnsureSchema`).
- `DefaultWorkflowSynthesizer.PromoteAsync` validates and writes a candidate. It does not call `WorkflowPromotionEvaluator`, and nothing in the repository produces the evaluations that class expects (`src/Ampelum.Agent.Synthesis/DefaultWorkflowSynthesizer.cs`, lines 115 to 136).

MCP

- `HttpMcpTransport` is superseded by `SdkMcpTransport` and kept only because it is public API. It sends bare JSON-RPC POSTs with no `initialize`, so it works against a hand-written server but not a standard one, and it calls `tools/list` once and does not follow the next-cursor field, so a server that paginates returns only its first page (`src/Ampelum.Agent/Mcp/McpModels.cs`, `ListToolsAsync`). `SdkMcpTransport` has neither limit.
- `McpToolSource.SourcePrefix` is the host of the server address only, so two MCP servers on one host (for example `127.0.0.1` on ports 1 and 2) share the prefix `mcp/127-0-0-1` and their tool names collide. `RegisterHandlersAsync` throws, naming both addresses, instead of letting the second server take over the first one's handlers; connect each such server to its own `ToolDispatcher` and `ToolLoader`. Call `RegisterHandlersAsync` once per connection: a second call registers the same names again but does not remove the handler of a tool the server has since dropped.
- `SdkMcpTransport` opens its connection on the first call, with the SDK's default initialization timeout, and passes no OAuth settings: only an optional bearer key is supported for HTTP.
- `McpServerAdapter` has no transport in `src`, so Ampelum can call MCP servers but cannot be served as one without your own transport. The agent service sample writes a minimal JSON-RPC over HTTP one.

A2A, loaders and batch

- `A2AClient` writes the message role and task state as numbers, not as the names other agents expect, and `A2ATask` requires `CreatedAt` and `UpdatedAt`, so a response without them fails with a `JsonException`. It reads any number as a role or state, also one that is no member of the enum (`src/Ampelum.Agent/A2A/A2AClient.cs`, `A2ATask.cs`).
- `OpenAIBatchSubmitter` maps the `cancelling` state to `Cancelled`, and `StreamResultsAsync` throws when the batch has no output file.

Host

- `rebuild-index --config <path>` ignores the path. `Program` passes the whole argument list, so the first argument is `rebuild-index` and the `--config` check never matches; the command always reads `appsettings.json` from the working directory (`src/SignalGrid.Ampelum.Host/Program.cs`, line 17, and `src/SignalGrid.Ampelum.Host/Tools/HostCli.cs`, line 20).

- The optimize progress stream keeps no history. `OptimizerProgressBus.Publish` drops an event when no client is subscribed yet, so a client that connects after a run has started misses the earlier progress (`src/SignalGrid.Ampelum.Host/Optimize/OptimizerProgressBus.cs`).
- `ProblemDetailsMiddleware` is internal and nothing registers it, so an unhandled endpoint exception is not mapped to a Host error code.
- The Host optimize endpoint scores with a private `NoOpMetric` that returns 1.0, so it does not measure quality (`src/SignalGrid.Ampelum.Host/ModuleRegistry/PredictComposedAdapter.cs`).
- `POST /modules/{name}/forward` and `/stream` apply the instruction of the active prompt version (or of the version named in `PromptVersionId`), not its demonstrations. Prompt demo examples read back with null fields, and `rebuild-index` drops prompt versions created through the endpoint.
- Audit lookup by trace id returns 404 until the index is rebuilt. Replay of a `Predict` call finds the module by the audited name `Predict`, or else the one module whose signature accepts the recorded inputs; with two such modules and none named `Predict` it answers `module_not_found` (ambiguous).
- The Host `rebuild-index` command fails when the working directory holds an `appsettings.json`.
- `/readyz` answers 503 when one SQLite open takes longer than 50 ms, which can happen on a cold start, so a client must poll it (`src/SignalGrid.Ampelum.Host/Persistence/HostSqliteConnectionFactory.cs`, line 39).
- `ChatAdapter.OnChunk` treats the closing `[[ ## completed ## ]]` marker as a field. The Host stream endpoint filters it out, but a direct `Predict.StreamAsync` caller sees an update named `completed`.
- A chunk that carries the last tokens and that marker together is not sent as a `partial`, so the last `partial` can lag the text. The `final` frame is always complete.


Written but not called by shipped code

- `WorkflowPromotionEvaluator`, `CronPortfolioScheduler`, `NullOptimizerRunner` in the Host, and the Host option `Host:Modules:RegistryFile`, which the code marks as reserved. They are public API and most have tests, but no other code in this repository uses them.
- `ConfiguredModelRouter` with `ModelRoutingTable` and `DurableAgentRuntime` are used only by the agent runtime sample, and `IExecutionSandbox` with `ProcessSandbox` only by the safety gateway sample.

- `PhoenixCredentials` is accepted by `AddPhoenixExporter` and nothing reads it. Delivery and its API key belong to the OTLP exporter the host configures.

Audit and safety

- All `AuditHook`s over one sink share one append gate that is not reentrant: a sink must not call back into an `AuditHook` on itself from `AppendAsync`, or the append waits for itself.
- `StreamingChatSession` sends each token to the caller before the after hooks run. The hooks get the whole reply as outputs (`text`), so `ModerationHook` checks it, but it can only fail the turn after the flagged text was delivered.
- `FileTrustStore` writes one file per agent type and domain, but a violation only demotes one level and never revokes trust, and a file another store changes after this one read it is not re-read. A file whose level is not one of the defined names (a number, another case, nothing) throws `InvalidDataException` naming the file.

Caching

- `SemanticCacheHook` matches a reworded question only when an embedding generator is registered; without one it is an exact match on the module name, signature type and inputs. Its vectors are kept in the hook (4096 at most), not in the `ISemanticCache` store. A failing embedding call is skipped, not raised, and a streamed turn is never served from or stored in the cache.
- An entry `SemanticCacheHook` stores while the embedding service fails has no vector, so it is found only by exact match afterwards; there is no retry. The skipped call is visible as `HookMetadataKeys.SemanticCacheEmbeddingFailed` on the call's context.
- A `TokenCachedChatClient` hit is a JSON copy of the stored response: `AdditionalProperties` values and function-call arguments come back as `JsonElement` rather than their original types, and `RawRepresentation` is not carried. A response the serializer cannot copy is returned uncached.
- The `SemanticCacheHook` similarity text is all inputs joined, so two long inputs that differ in one detail can pass the threshold, and the 0.95 default is not calibrated against a real embedding model.

Routing and tracing

- A streamed call through `RoutedChatClient` fails over only before its first update. A stream that fails after an update was returned reaches the caller, because a partial stream is not retried.
- The `AddLangFuseExporter`, `AddLangSmithExporter` and `AddPhoenixExporter` calls wrap the prior `ITraceSink`, and the exporter never disposes it, so a batched inner sink is not flushed by disposing the exporter.

Retrieval and vector stores

- `HybridRetriever` passes a fixed `k: 60` to reciprocal rank fusion, so a configured value has no effect (`src/Ampelum/Retrieval/Hybrid/HybridRetriever.cs`).
- The pgvector backend interpolates the table name and the filter key into SQL. Filter values are parameters, so treat table names and filter keys as trusted input.
- Qdrant replaces a non-UUID id with an MD5-derived UUID and returns that, so the original id is lost. Non-canonical UUID forms come back canonical.
- `EmbeddingRetriever` puts the score the vector store computed on each document, so a score floor works; the scale is the store's own (`InMemoryVectorStore` gives (cosine + 1) / 2, other adapters their own). pgvector returns metadata as `JsonElement`, the other adapters as strings.
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

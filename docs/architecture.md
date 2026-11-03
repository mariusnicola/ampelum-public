# Architecture

Ampelum is signature-driven at every level. A signature describes one LM interaction as typed inputs and outputs, and modules, optimizers, evaluators and retrieval patterns are all built from that one idea.

```
User code (signatures, composition, optimizers)
        |
Orchestration    Sequence, Loop, ConditionalBranch, ErrorHandler
Composition      BestOfN, MultiChainComparison, Parallel, Refine
Modules          Predict, ChainOfThought, ReAct, DynamicPredict
        |
Cross-cutting    hooks, adapters, budget, audit, safety
        |
IChatClient      any provider
```

## Signatures and modules

A record implementing `ISignature` carries a `[Signature]` description, with `[Input]` and `[Output]` on its properties. Ampelum formats the prompt, calls the model through an adapter, parses the reply and returns a typed prediction.

| Module | Purpose |
|---|---|
| `Predict<TSig>` | One LM call, with streaming and the full hook pipeline |
| `ChainOfThought<TSig>` | Adds a reasoning output before the answer |
| `ReAct<TSig>` | Reason and act loop that calls tools for up to N iterations |
| `DynamicPredict` | Untyped predict for signatures known only at runtime |
| `StreamingChatSession` | Free-form streaming chat with tools |
| `TaskAgent` | Background agent wrapping a chat session in a completion loop |

## Adapters

`ChatAdapter` uses `[[ ## FieldName ## ]]` section markers in plain text. `JsonAdapter` builds a JSON schema from the output fields and has strict and lenient parsing modes.

## Hook pipeline

Every `Predict` call runs the same order:

```
OnBefore (ascending) -> Adapter.Format -> LM call -> Adapter.Parse -> OnAfter (descending)
```

Budget, audit, retry, moderation, PII redaction, content policy, prompt injection, tool gating and semantic cache are all hooks. A hook that fills in the outputs during `OnBefore` short-circuits the call. `Predict` and `StreamingChatSession` run the pipeline directly. `ReAct`, `DynamicPredict`, `MultiChainComparison`, the `Copro`, `GEPAOptimizer` and `InferRules` optimizers and the retrieval patterns run it through `HookedLmCall`, which wraps a plain chat call in the same hooks.

## Evaluation and optimizers

`Evaluate<TSig>` runs a program across a labeled dataset and scores it with metrics such as `ExactMatch`, `F1`, `Bleu`, `RougeN`, `RougeL` or an LLM judge. Optimizers then use those scores to improve the prompt: `BootstrapFewShot`, `Copro`, `MiproV2`, `GEPAOptimizer`, `KnnFewShot` and others. Optimized programs persist through `IProgramPersistence`. `BootstrapFewShot` currently ignores its metric; see Known limits in the [root README](../README.md).

`MiproV2` works in four steps. It bootstraps several demo sets from seed-shuffled copies of the trainset and adds the empty set as a candidate. It asks the proposer client for a dataset summary, then for instruction candidates per submodule, each grounded in the summary, the submodule name, one demo set and one tip chosen by the seeded random generator; the current instruction is always a candidate. It then runs a seeded Tree-structured Parzen Estimator over the (instruction, demo set) choice of every submodule: the first fifth of the trials are random, later ones favour the choices that scored well. Each trial is scored on a random minibatch of the validation set (`valset`, or the trainset when none is given), and every `fullEvalEvery` trials and at the end the best combination by minibatch mean is scored on the whole set. The returned program is the best full-evaluation combination. A runner or metric exception on one example scores 0 for it and is counted in the `optimizer.runner_errors` activity tag and the `RunnerErrors` extra of each progress report. Every proposer call runs through the hook pipeline under a module name starting `MIPROv2.`. Bootstrapped demos come from `BootstrapFewShot`, so they are the program's own successful runs, not only the correct ones.

`GEPAOptimizer` keeps a pool of candidates, each a set of instruction overrides with its parent ids and its score on every validation example (`valset`, or a seeded split of the trainset). Candidate 0 is the unchanged program. The Pareto frontier is the set of candidates that are best on at least one validation example; the parent of an iteration is drawn from it with a seeded generator, weighted by the number of examples the candidate is best on. A mutation picks the submodule round robin, runs the parent on a seeded minibatch of the trainset and asks the reflection client for a new instruction, given the current instruction and each example's inputs, outputs and feedback. The feedback comes from an `IFeedbackMetric`, or, for a plain `IMetric`, is built from the expected and actual outputs. The child runs on the same minibatch, and only if it scores higher than the parent does it run on the whole validation set and join the pool. Every few iterations a merge combines two frontier candidates that share an ancestor (neither descends from the other): per submodule the child takes the instruction of the side that changed it since the closest common ancestor, and it is gated on the minibatch against the better parent. The program runs through a caller-supplied runner, the same shape `MiproV2` takes. The search stops after `MaxIterations` or when `MaxMetricCalls` example runs are spent: no run starts past the cap and a candidate whose evaluation is cut short is not added, so the result is the best fully evaluated candidate, as instruction overrides to bind with `OptimizedProgram.Bind`. A runner or metric exception scores 0 for that example and is counted like in `MiproV2` (a NaN score counts as 0, and a timeout that does not cancel the caller's token is such an exception too); a failed reflection call throws `OptimizerException`. Every reflection call runs through the hook pipeline under the module name `GEPA.Reflect`.

## Retrieval

`Ampelum.Retrieval` holds the retrievers, patterns (`HydeRetriever`, `MultiQueryRetriever`, `StepBackRetriever`, `SubQuestionRetriever`, `QueryRouter`), document loaders, text splitters and retrieval metrics. Reranking and the vector stores are separate packages, so you only take the dependencies you use.

## Audit and observability

The audit trail is a SHA-256 hash chain, so `AuditChainVerifier` can detect tampering with recorded entries. Traces and metrics use `ActivitySource` and `Meter`, both named `SignalGrid.Ampelum`. LangFuse, LangSmith and Phoenix integrations are optional packages.

## Agents and tools

`Ampelum.Agent` adds the pieces for long-running autonomous work: governed tool dispatch through `ToolDispatcher`, agent identity, skills, trust and modes, secret stores, A2A, and MCP. Generic contracts live in the dependency-free `Ampelum.Agent.Contracts`.

There are two MCP paths on purpose. `Ampelum.Mcp` is a thin `AIFunction` bridge with no governance. The MCP integration in `Ampelum.Agent` goes through the governed runtime, behind `IMcpTransport`.

## Durable workflows

`DurableWorkflowRuntime` executes declarative workflow definitions with checkpointed state, so a run can survive a restart. Step types come from `WorkflowStepKind`. The runtime dispatches `Sequential`, `Branch`, `Loop`, `Tool`, `FanOut` and `Join` to your handler. `Approval` steps pause the execution until `DecideAsync` approves or rejects them, and a handler can return `DurableWorkflowNodeResult.Wait` to pause any step the same way. `Wait` is accepted and runs through the handler. Four kinds are runtime primitives that never reach the handler, except for compensations. `WaitForSignal` parks the node until `SignalAsync` delivers its signal (a signal that arrives early is kept on the node); dependent handlers read it from `DurableWorkflowNodeContext.Dependencies`. `Evaluate` calls the `IDurableWorkflowEvaluator` you pass to the constructor, stores the observations on the node, writes one `node.evaluated` audit event listing every metric, and fails when a plan metric is missing or an observed metric misses the threshold the workflow objective gives it. `EmitEvent` writes the audit event `event.<name>` in the same write that marks the node done. Both are atomic with the node only on a transactional store (SQLite, PostgreSQL), not on `InMemoryWorkflowStateStore` with a separate sink. `Compensate` steps never run in the forward flow; when the execution would fail, the runtime runs, through your handler, the compensation of every succeeded step that names one, newest completion first, and then fails the execution. Cancellation does not compensate. See Known limits in the [root README](../README.md).

- Persistence is `SqliteWorkflowPersistence`, `PostgresWorkflowPersistence` from `Ampelum.Agent.Postgres`, or the in-memory `InMemoryWorkflowStateStore` for tests.
- `Ampelum.Agent.Yaml` compiles YAML definitions with `WorkflowYamlCompiler`.
- `Ampelum.Agent.Synthesis` generates workflow candidates in two phases. `DefaultWorkflowSynthesizer` produces an immutable candidate, and only `PromoteAsync` writes it. A candidate is marked as needing review only when it changes the step structure or the measured success rate is below 0.5. `PromoteAsync` refuses such a candidate until you pass `reviewApproved: true`, and writes any other candidate without a review step.
- `WorkflowPolicyGate` applies tool policy to workflow steps, and `PortfolioOrchestrator` coordinates work across a dependency graph.

## Hosting and tooling

`SignalGrid.Ampelum.Host` is an ASP.NET Core server exposing modules (`/modules`), prompts with versioning (`/prompts`), optimizer runs (`/optimize`), audit (`/audit`), health checks (`/healthz`, `/readyz`) and, when enabled, `/admin`. See [configuration](configuration.md) for its settings.

`Ampelum.Cli` provides the `new`, `eval`, `bench` and `repl` commands, and `Ampelum.Templates` holds the `dotnet new` templates. `Ampelum.Testing` gives you `FakeChatClient`, so tests need no network. No package picks a provider or a model for you: see [configuration](configuration.md). The source generator and `Ampelum.Analyzers` move signature checks to compile time and support AOT publishing.

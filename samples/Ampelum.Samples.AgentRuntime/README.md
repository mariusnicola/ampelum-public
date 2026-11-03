# Agent runtime: triage a backlog, stop, resume

A long engineering task over a small demo project held in this sample (`DemoProject/`, extracted to a temp folder at run time): triage three open issues, edit the report, wait for a person, then post the report. The run is planned into a workflow, executed, stopped at the approval step and finished by a second runtime that reads the stored state. Both runtimes live in one OS process.

What it shows, with the file that runs it:

- Planning: `DefaultWorkflowSynthesizer` asks the model for workflow YAML, validates it through `WorkflowYamlCompiler` and writes it only after it compiles (`TriagePlanner.cs`). `PortfolioIntentDecomposer` splits the goal into one audit task per backlog issue.
- Workflow kinds: the plan uses `FanOut` (assess), `Join` (collect), `Loop` (refine, bounded by `max_iterations`), `Approval` (signoff), `Wait` (settle) and a `Tool` step (`Fake/plan.yml`, `TriageSteps.cs`).
- Interrupt and resume: `DurableAgentRuntime` pauses the run at the approval step and records a resume token. The first runtime is stopped by blocking its handler. A second runtime, with new objects on the same state folder, resumes with the token. Finished steps are not run again and no model call is repeated (`TriageProcess.cs`, `TriageSteps.cs`).
- Event log: the run events (`Started`, `CheckpointSaved`, `InterruptRequested`, `CheckpointSaved`, `Resumed`, `CheckpointSaved`) are stored as JSON files and replayed with `StreamAsync`.
- State machine and monitor: `WorkflowStateMachine` tracks phase and waiting state (`RunSupervisor.cs`); `DefaultWorkflowMonitor` scores the finished run, and an issue deferred for budget counts as a failed outcome with a risk event (`TriageProcess.cs`).
- Memory: `MemoryManager` archives the finished phase and rebuilds the context with the goal first, over a JSON file store, so a report written after a restart still states the goal (`PhaseNotes.cs`). The Context subsystem (`InMemoryEventStore`, `CostLedger`, `HygienePipeline`, `TalkSelector`, `DefaultContextRenderer`) keeps this process's working memory: it stores one copy of a file that two issues read, and totals token usage (`RunJournal.cs`).
- Skills: `SkillRegistry` holds the skills of `skills.json`. `TagSkillSelector` picks a skill that carries every label of an issue, `LlmSkillSelector` asks the model when none does, and `SelectByTags` is the last resort. Deprecated skills are never offered (`TriageSkills.cs`).
- Model tiers: `ConfiguredModelRouter` maps a task type to fast, balanced or capable from `DemoProject/triage-policy.json`. The model id of the tier is set on every call (`ModelRouting.cs`, `ModelClients.cs`).
- Governance: the project policy sets the model call budget, token budget, readable folders and grant lifetime. A budget refusal defers the issue and the run goes on; the report lists it. The report tool is `post_report`, which needs confirmation: it runs only with a `SessionGrant`, and the grant is created after the approval (`TriageTools.cs`, `TriageSteps.cs`, `ModelClients.cs`).
- Portfolio and workspace: the project is registered in `JsonFileProjectRegistry`, with a blast radius from `SoftwareDomainProfile`, and `PortfolioOrchestrator` runs the audit tasks through the tool dispatcher (`TriageWorkspace.cs`, `TriageSteps.cs`).
- Conventions and code facts: `DefaultConventionEnforcer` reads the project's `.ampelum-conventions.yaml`; `CodeIndexer`, `IndexStore` and `CodeGraph` give the type names and imports behind each issue (`CodeFacts.cs`).
- Contracts: `PromptCompiler` compiles the brief with a stable hash, builds the audit event and the context manifest that lists which project files the caller may use (`IssueAuditor.cs`).

## Run

Without a key, using a scripted model. No network, and the state it writes lives in a temp folder that is removed:

```
dotnet run --project samples/Ampelum.Samples.AgentRuntime -c Release -- --fake
```

Exit code 0 means the run succeeded and the report exists.

Against a live model: copy `.sample.env` to `.env` and set `OPENROUTER_API_KEY`, `OPENROUTER_MODEL_1` (balanced and capable tiers, and the planner) and `OPENROUTER_MODEL_2` (fast tier). Then run the same command without `--fake`. These three are the only settings the sample reads. The budget in `DemoProject/triage-policy.json` (`maxTokens` 2000) is sized for the scripted model, which books 100 tokens per call. A real model uses more per call and would defer most issues; the file is the place to change it, and the live path has not been run.

Tests: `dotnet test tests/Ampelum.Samples.AgentRuntime.Tests`. They need no key and no network and read no setting.

## What `--fake` proves

- The code path runs end to end: plan, fan out, join, loop, approval stop, restart, resume, tool call, report.
- The event log, the stored node attempts, the budget refusals, the tier chosen for each call, the grant rule and the memory behaviour are what the tests assert. Each assertion was checked to fail when the sample's use of the feature was broken.

## What `--fake` does not prove

- Nothing about a real model. The answers are scripted in `Fake/`. Output quality, prompt wording, the YAML a real model writes and the skill a real model picks are untested. The live path has not been run.
- The two runtimes are two sets of objects in one operating system process. The first is stopped by blocking its handler, not by killing it.
- Token counts are the scripted 100 per call.

## Limits of the library that this sample works around

- All step kinds share one handler in `DurableWorkflowRuntime`, and nothing pauses for `Approval` or `Wait`. `Loop` does have continue and max-iteration logic and `Join` has readiness logic, which the sample uses. The pause comes from `DurableAgentRuntime.PauseAsync`, called inside the approval handler.
- `DurableAgentRuntime.PauseAsync` followed by `ResumeAsync` on the same instance throws "Cannot resolve escalation from state Running", because pausing does not escalate the internal state machine. The sample resumes with a fresh instance, which restores the machine from the stored snapshot.
- `SessionBudget` has `IsExhausted` and `IsWarning`; the sample calls `IsExhausted`, but nothing in src enforces the budget. The sample's `ModelBudget` and `BudgetedChatClient` do.
- `SkillRegistry.LoadAsync` does nothing, so `skills.json` is read by the sample.
- `JsonFileProjectRegistry` stores label, root and escalation policy only. The blast radius and the allowed tools are derived again on load.
- The code indexer's `ExtractCalls` exists but covers only Method or Function symbols, and the method pattern misses plain return types, so in practice the index holds type declarations and imports. The facts hold no line numbers or callers.
- `ISynthesisInfrastructure` has no implementation in the library; `ChatSynthesisInfrastructure.cs` supplies one.
- `EmbeddingEngine`, `ContextAssembler` and `IndexerPipeline` are not used. `EmbeddingEngine` starts a model download from huggingface.co when first touched, so its SHA256 stub fallback (`StubEmbed`) cannot be shown without network and machine state.

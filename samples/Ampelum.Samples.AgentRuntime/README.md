# Agent runtime: triage a backlog, stop, resume

A long engineering task over a small demo project held in this sample (`DemoProject/`, extracted to a temp folder at run time): triage three open issues, edit the report, wait for a person, then post the report. The run is planned into a workflow, executed, stopped at the approval step and finished by a second runtime that reads the stored state. Both runtimes live in one OS process.

What it shows, with the file that runs it:

- Planning: `DefaultWorkflowSynthesizer` asks the model for workflow YAML, validates it through `WorkflowYamlCompiler` and writes it only after it compiles (`TriagePlanner.cs`). `PortfolioIntentDecomposer` splits the goal into one audit task per backlog issue.
- Workflow kinds: the plan uses `FanOut` (assess), `Join` (collect), `Loop` (refine, bounded by `max_iterations`), `Approval` (signoff), `Wait` (settle) and a `Tool` step (`Fake/plan.yml`, `TriageSteps.cs`).
- Interrupt and resume: `DurableWorkflowRuntime` parks the `Approval` step itself: the handler is not called, the node is `Waiting` and the execution reports `AwaitingConfirmation`. The first runtime's `RunAsync` returns and `DurableAgentRuntime` records the pause and a resume token. A second runtime, with new objects on the same state folder, resumes the run record with the token, delivers the approval with `DurableWorkflowRuntime.DecideAsync` and continues with `ResumeAsync`. Finished steps are not run again and no model call is repeated (`TriageProcess.cs`, `TriageSteps.cs`).
- Event log: the run events (`Started`, `CheckpointSaved`, `InterruptRequested`, `CheckpointSaved`, `Resumed`, `CheckpointSaved`) are stored as JSON files and replayed with `StreamAsync`.
- State machine and monitor: `WorkflowStateMachine` tracks phase and waiting state (`RunSupervisor.cs`); `DefaultWorkflowMonitor` scores the finished run, and an issue deferred for budget counts as a failed outcome with a risk event (`TriageProcess.cs`).
- Memory: `MemoryManager` archives the finished phase and rebuilds the context with the goal first, over a JSON file store, so a report written after a restart still states the goal (`PhaseNotes.cs`). The Context subsystem (`InMemoryEventStore`, `CostLedger`, `HygienePipeline`, `TalkSelector`, `DefaultContextRenderer`) keeps this process's working memory: it stores one copy of a file that two issues read, and totals token usage (`RunJournal.cs`).
- Skills: `SkillRegistry` holds the skills of `skills.json`. `TagSkillSelector` picks a skill that carries every label of an issue, `LlmSkillSelector` asks the model when none does, and `SelectByTags` is the last resort. Deprecated skills are never offered (`TriageSkills.cs`).
- Model tiers: `ConfiguredModelRouter` maps a task type to fast, balanced or capable from `DemoProject/triage-policy.json`. The model id of the tier is set on every call (`ModelRouting.cs`, `ModelClients.cs`).
- Governance: the project policy sets the model call budget, token budget, readable folders and grant lifetime. A budget refusal defers the issue and the run goes on; the report lists it. The report tool is `post_report`, which needs confirmation: it runs only with a `SessionGrant`, and the grant is created after the approval (`TriageTools.cs`, `TriageSteps.cs`, `ModelClients.cs`).
- Portfolio and workspace: the project is registered in `JsonFileProjectRegistry`, with a blast radius from `SoftwareDomainProfile`, and `PortfolioOrchestrator` runs the audit tasks through the tool dispatcher (`TriageWorkspace.cs`, `TriageSteps.cs`).
- Conventions and code facts: `DefaultConventionEnforcer` reads the project's `.ampelum-conventions.yaml`; `CodeIndexer`, `IndexStore` and `CodeGraph` give the type names and imports behind each issue (`CodeFacts.cs`).
- Coding sub-agent: after the report is posted, `SubAgentRunner` runs a coding task on issue #102 ("Pricing.ApplyDiscount never rounds") in the extracted temp copy of the demo project. The model uses the runner's file tools (`read_file`, `write_file`) and ends on `DONE:`. The sample does not trust that claim: it reads `src/Pricing.cs` back and checks that the runner recorded a write to it, that the file changed, still declares `ApplyDiscount`, and that `ApplyDiscount` now returns a `Math.Round(...)` call. This is a text match; the edited file is not compiled and the rounded value is not checked. The turn limit is 6 (`IssueFixer.cs`, `Fake/FakeFixerModel.cs`, `TriageScenario.cs`).
- Contracts: `PromptCompiler` compiles the brief with a stable hash, builds the audit event and the context manifest that lists which project files the caller may use (`IssueAuditor.cs`).

## Run

Without a key, using a scripted model. No network, and the state it writes lives in a temp folder that is removed:

```
dotnet run --project samples/Ampelum.Samples.AgentRuntime -c Release -- --fake
```

Exit code 0 means the run succeeded, the report exists and the sub-agent's fix passed the sample's check of `src/Pricing.cs`.

Against a live model: copy `.sample.env` to `.env` and set `OPENROUTER_API_KEY`, `OPENROUTER_MODEL_1` (balanced and capable tiers, and the planner) and `OPENROUTER_MODEL_2` (fast tier). Then run the same command without `--fake`. These three are the only settings the sample reads. The sub-agent uses the `OPENROUTER_MODEL_1` model. Its calls are not counted by the triage budget; only the turn limit bounds them. The budget in `DemoProject/triage-policy.json` (`maxTokens` 2000) is sized for the scripted model, which books 100 tokens per call. A real model uses more per call and would defer most issues; the file is the place to change it, and the live path has not been run.

Tests: `dotnet test tests/Ampelum.Samples.AgentRuntime.Tests`. They need no key and no network and read no setting.

## What the coding sub-agent can reach on this machine

Read from `src/Ampelum.Agent/Execution/SubAgentRunner.cs`. The runner has no shell. It starts no process, makes no network call and reads no environment variable; the file has no code for any of these. Its tools are `read_file`, `write_file`, `list_directory`, `glob` and `grep`, all plain .NET file calls. Its system prompt also names `edit_file`, `build` and `run_tests`, but none of them exists, so a call to one comes back as an error result and nothing runs. The sample's scripted sub-agent therefore runs no shell command, and no test can show a shell command leaving a directory because no command can be run.

Limits that do exist, each pinned by a `KnownLimit_...` test in `SubAgentTests.cs`:

- The working directory is a prefix and not a fence. A relative path is joined to it, an absolute path is used as given, and `..` is not checked. `write_file` can create or overwrite any file the process user may write, and create missing folders, outside the working directory (`KnownLimit_AbsolutePathWrite_LeavesTheWorkingDirectory`, `KnownLimit_DotDotPathWrite_LeavesTheWorkingDirectory`). The tests only write inside their own temp folder.
- `read_file` and `grep` read any file the process user may read, outside the working directory too (`KnownLimit_ReadFileAndGrep_ReachFilesOutsideTheWorkingDirectory`). `list_directory` lists and `glob` searches folders outside it too (`KnownLimit_ListDirectory_ListsAFolderOutsideTheWorkingDirectory`, `KnownLimit_Glob_FindsFilesOutsideTheWorkingDirectory`). `write_file` with `append` goes through the same path rule and extends a file outside it (`KnownLimit_WriteFileAppend_ExtendsAFileOutsideTheWorkingDirectory`).
- No limit on file size, number of files or bytes written, and no deny list. Only the turn limit bounds a run.
- The runner offers its tools but does not run the calls a model makes. The chat client it is given must do that. With a plain client, the calls are never run, nothing is written and the run can still end on `DONE:` (`KnownLimit_ChatClientWithoutToolInvocation_NeverRunsTheCallsAndStillReportsDone`). The sample wraps the client in `FunctionInvokingChatClient` for this reason (`IssueFixer.WithToolInvocation`).
- The tools list does not match the prompt (`KnownLimit_NoShellOrBuildTool_ThePromptStillNamesBuildAndRunTests`, `KnownLimit_ToolCallToABuildTool_IsNotRunAndTheRunnerCarriesOn`).
- A failed write is still listed in `FilesChanged` (`KnownLimit_FailedWrite_IsStillListedAsAChangedFile`), and paths are listed as the model spelled them, so the sample compares full paths. `DONE:` is the model's word; this is why the sample reads the file back.
- The tool parameters have no defaults: a call that leaves out an optional one such as `offset` fails with "Function failed". The scripted calls send them as null.
- The prompt's "Working directory:" line prints the task id, not the directory.
- `SubAgentTask.Tier` is ignored by the runner; the sample picks the model by the client it passes.

For a live model these file limits apply as they are. The sample gives it the extracted temp copy as the working directory and asks it to touch only `src/Pricing.cs`, which is a request and not a barrier.

## What `--fake` proves

- The code path runs end to end: plan, fan out, join, loop, approval stop, restart, resume, tool call, report.
- The event log, the stored node attempts, the budget refusals, the tier chosen for each call, the grant rule and the memory behaviour are what the tests assert. Each assertion was checked to fail when the sample's use of the feature was broken.

## What `--fake` does not prove

- Nothing about a real model. The answers are scripted in `Fake/`. Output quality, prompt wording, the YAML a real model writes and the skill a real model picks are untested. The live path has not been run.
- The two runtimes are two sets of objects in one operating system process. The first one returns because the workflow is parked; it is not killed, so a crash while a node is waiting is covered by the library tests and not by this sample.
- Token counts are the scripted 100 per call.
- The sub-agent's turns are a fixed script, tool calls included (read the file, write it back rounded, say `DONE:`). It proves that the runner's tools change the file when the calls are run, that the run ends on `DONE:`, that the turn limit stops a run that never says it, and that the sample's check fails when the write is missing. It does not prove that a real model finds the file, writes valid C# or rounds correctly. The check is textual; the edited file is not compiled.

## Limits of the library that this sample works around

- `SubAgentRunner` (`src/Ampelum.Agent/Execution/SubAgentRunner.cs`): no shell, no build or test tool though the prompt names them (`BuildSystemPrompt`), no containment of paths in `Resolve`, tool calls not run unless the client invokes functions, tool parameters without defaults, and the prompt line "Working directory" printing the task id. Details in the section above.

- `DurableWorkflowRuntime` pauses for `Approval` steps only. Every other kind, `Wait` included, goes to the one shared handler, and the sample's handler returns success for `Wait` at once. `Loop` does have continue and max-iteration logic and `Join` has readiness logic, which the sample uses.
- `DurableAgentRuntime.PauseAsync` followed by `ResumeAsync` on the same instance throws "Cannot resolve escalation from state Running", because pausing does not escalate the internal state machine. The sample resumes with a fresh instance, which restores the machine from the stored snapshot.
- `SessionBudget` has `IsExhausted` and `IsWarning`; the sample calls `IsExhausted`, but nothing in src enforces the budget. The sample's `ModelBudget` and `BudgetedChatClient` do.
- `SkillRegistry.LoadAsync` does nothing, so `skills.json` is read by the sample.
- `JsonFileProjectRegistry` stores label, root and escalation policy only. The blast radius and the allowed tools are derived again on load.
- The code indexer's `ExtractCalls` exists but covers only Method or Function symbols, and the method pattern misses plain return types, so in practice the index holds type declarations and imports. The facts hold no line numbers or callers.
- `ISynthesisInfrastructure` has no implementation in the library; `ChatSynthesisInfrastructure.cs` supplies one.
- `EmbeddingEngine`, `ContextAssembler` and `IndexerPipeline` are not used. `EmbeddingEngine` starts a model download from huggingface.co when first touched, so its SHA256 stub fallback (`StubEmbed`) cannot be shown without network and machine state.

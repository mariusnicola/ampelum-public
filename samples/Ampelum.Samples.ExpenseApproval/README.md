# Expense approval

A durable workflow, defined in `expense-approval.yml`, that is stopped after its first step and resumed by a new runtime.

What it shows:

- The workflow is YAML, compiled with `WorkflowYamlCompiler`. The approval limit lives in the YAML, not in C#.
- A model extracts vendor, amount and category from free text with `Predict`.
- A `Branch` step picks the auto-pay tool or the flag-for-review tool by amount, and a `Join` closes the flow.
- Tool steps go through `WorkflowToolStepHandler`, `WorkflowPolicyGate`, `ToolDispatcher` and `PolicyEngine`. A tool outside a step's `tool_allow_list` is blocked before it runs.
- The run is persisted in SQLite. After the first step the run is stopped, then a new runtime and a new persistence on the same database resume it. The finished extraction is not repeated.
- After the join, four more step kinds run as runtime primitives. None of them reaches the handler in `ExpenseApprovalPipeline.cs`, except the compensation:
  - `receipt` (`WaitForSignal`): the execution parks as `AwaitingConfirmation` until `DurableWorkflowRuntime.SignalAsync` delivers the `receipt` signal, with the receipt amount as payload. `DeliverReceiptAsync` does that on a new runtime and persistence.
  - `check` (`Evaluate`): `ReceiptEvaluator.cs` compares the receipt amount, read from the signal stored on the `receipt` node, with the extracted amount, read from the `extract` node. Both come from `DurableWorkflowNodeContext.Dependencies`. The result is the `receipt_matches` metric, and the objective's threshold decides whether the node passes.
  - `announce` (`EmitEvent`): writes the audit event `event.expense.settled` in the same write that marks the step done, so on SQLite and PostgreSQL it exists once even across a crash.
  - `refund` (`Compensate`): the `pay` step names it with `compensation_step`. It runs only when the execution would fail after a payment, here when the receipt does not match. The handler calls the ledger's `RefundAsync`. A successful run ends with it `Skipped`.
- The third note in `--fake` has a receipt for another amount. Its payment is refunded and the execution ends `Failed`; that is the outcome the run expects, and the exit code stays 0.

The same run can keep its checkpoints and audit history in PostgreSQL instead, through `PostgresWorkflowPersistence`. `ExpenseStore.cs` picks the store: without `--postgres` it is the SQLite file, with `--postgres` it is PostgreSQL. Each runtime gets a new persistence object, so the second runtime resumes from the rows in the database, not from anything in memory.

The demo stops by blocking a handler inside one process, not by killing it. The receipt is delivered by the same process, through a new runtime and persistence; a real scanner or person would be another process.

Each tool call carries the node's idempotency key and the ledger ignores a repeat of a key it has seen. The dispatcher hands a registered handler only its arguments, so the key reaches the handler as the `idempotency_key` argument. A payment is protected only if the ledger you plug in honours that key.

An amount that is not a positive number, or is over the limit in the YAML, goes to `flag_for_review`; it is never paid.

Run without a key, using a scripted model:

```
dotnet run --project samples/Ampelum.Samples.ExpenseApproval -c Release -- --fake
```

Run with the scripted model and a real PostgreSQL database:

```
dotnet run --project samples/Ampelum.Samples.ExpenseApproval -c Release -- --fake --postgres
```

`--postgres` needs `WORKFLOW_POSTGRES` (a connection string) in `.env`, `appsettings.json` or the environment. If it is empty the run fails and names the setting; there is no fallback to SQLite. The run deletes the rows it wrote (by execution id) when it ends, also after a failure.

What `--fake --postgres` proves: the first runtime stops after the extraction step, a second runtime built on a new `PostgresWorkflowPersistence` resumes from the stored rows, and the extraction is not run again. What it does not prove: it uses a scripted model, one process, and a database you own. It says nothing about several processes racing for the same execution.

Run against a live model: copy `.sample.env` to `.env` and set `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1`, then run the same command without `--fake`.

Tests: `dotnet test tests/Ampelum.Samples.ExpenseApproval.Tests`.

The tests in `PostgresWorkflowPersistenceTests` need a database. They read its connection string from the environment variable `AMPELUM_TEST_POSTGRES` only and fail, naming it, when it is empty. They are not skipped. Each test deletes the rows it wrote. CI has no database and leaves that class out:

```
dotnet test tests/Ampelum.Samples.ExpenseApproval.Tests --filter "FullyQualifiedName!~PostgresWorkflowPersistenceTests"
```

The live path (a real model) has never been run.

Library notes, for the step kinds:

- The workflow YAML declares the metric's `threshold` and an `evaluation` section that lists the metrics the `Evaluate` step must return; `expense-approval.yml` shows both, and `ExpenseApprovalPipeline.LoadDefinition` adds nothing to the compiled definition.
- A rejected `Approval` step normally fails the execution in the same write. When completed steps still owe compensations, `DecideAsync` leaves the execution `Running` and the next `ResumeAsync` compensates, then fails it (`DurableWorkflowRuntime.cs`, `DecideAsync`). This sample has no approval step.
- The `node.evaluated` audit line (every observed metric with its value and threshold result) and the `event.expense.settled` line are written by the same write that settles their node. That is atomic only on a transactional store such as `SqliteWorkflowPersistence` or `PostgresWorkflowPersistence`; with `InMemoryWorkflowStateStore` and a separate sink a crash between the two writes loses the line.

Library notes, for `PostgresWorkflowPersistence`:

- `src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs:20`: the class is `[Experimental("AMPELUM_V09_PREVIEW")]`, so the sample and its tests suppress that diagnostic in their project files.
- `src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs` (whole file, lines 21 to 255): there is no purge or delete API for an execution. The sample removes its rows with two `DELETE` statements on `ampelum_workflow_checkpoints` and `ampelum_workflow_runtime_audit`, by execution id.
- `src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs:31`, body at lines 139 to 188: the constructor calls `EnsureSchema`. It reads the catalog and runs DDL only when a table, the index or the `version` column is missing, so the first run against an empty database needs DDL rights. This sample makes several objects per run; after the first, each one only reads the catalog.
- `src/Ampelum.Agent.Postgres/Ampelum.Agent.Postgres.csproj:22`: `Ampelum.Agent` is pinned to `[0.8.0-rc3, 0.9.0)`. This is a lagging lower bound, not a break; the sample builds and runs against it.

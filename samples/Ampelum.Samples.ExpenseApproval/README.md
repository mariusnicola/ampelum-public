# Expense approval

A durable workflow, defined in `expense-approval.yml`, that is stopped after its first step and resumed by a new runtime.

What it shows:

- The workflow is YAML, compiled with `WorkflowYamlCompiler`. The approval limit lives in the YAML, not in C#.
- A model extracts vendor, amount and category from free text with `Predict`.
- A `Branch` step picks the auto-pay tool or the flag-for-review tool by amount, and a `Join` closes the flow.
- Tool steps go through `WorkflowToolStepHandler`, `WorkflowPolicyGate`, `ToolDispatcher` and `PolicyEngine`. A tool outside a step's `tool_allow_list` is blocked before it runs.
- The run is persisted in SQLite. After the first step the run is stopped, then a new runtime and a new persistence on the same database resume it. The finished extraction is not repeated.

The same run can keep its checkpoints and audit history in PostgreSQL instead, through `PostgresWorkflowPersistence`. `ExpenseStore.cs` picks the store: without `--postgres` it is the SQLite file, with `--postgres` it is PostgreSQL. Each runtime gets a new persistence object, so the second runtime resumes from the rows in the database, not from anything in memory.

The demo stops by blocking a handler inside one process, not by killing it.

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

Library notes, for `PostgresWorkflowPersistence`:

- `src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs:20`: the class is `[Experimental("AMPELUM_V09_PREVIEW")]`, so the sample and its tests suppress that diagnostic in their project files.
- `src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs` (whole file, lines 21 to 230): there is no purge or delete API for an execution. The sample removes its rows with two `DELETE` statements on `ampelum_workflow_checkpoints` and `ampelum_workflow_runtime_audit`, by execution id.
- `src/Ampelum.Agent.Postgres/PostgresWorkflowPersistence.cs:31`, body at lines 136 to 162: the constructor calls `EnsureSchema`, which runs DDL. Every new persistence object therefore needs DDL rights and runs `CREATE ... IF NOT EXISTS` statements, and this sample makes several objects per run. The `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` at line 160 takes an exclusive lock even when the column exists, so creating an object while another process writes to the same tables can fail with PostgreSQL error 40P01 (deadlock detected). Running these tests and the library's Postgres tests against one database at the same time showed it.
- `src/Ampelum.Agent.Postgres/Ampelum.Agent.Postgres.csproj:22`: `Ampelum.Agent` is pinned to `[0.8.0-rc3, 0.9.0)`. This is a lagging lower bound, not a break; the sample builds and runs against it.

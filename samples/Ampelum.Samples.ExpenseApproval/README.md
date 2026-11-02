# Expense approval

A durable workflow, defined in `expense-approval.yml`, that is stopped after its first step and resumed by a new runtime.

What it shows:

- The workflow is YAML, compiled with `WorkflowYamlCompiler`. The approval limit lives in the YAML, not in C#.
- A model extracts vendor, amount and category from free text with `Predict`.
- A `Branch` step picks the auto-pay tool or the flag-for-review tool by amount, and a `Join` closes the flow.
- Tool steps go through `WorkflowToolStepHandler`, `WorkflowPolicyGate`, `ToolDispatcher` and `PolicyEngine`. A tool outside a step's `tool_allow_list` is blocked before it runs.
- The run is persisted in SQLite. After the first step the run is stopped, then a new runtime and a new persistence on the same database resume it. The finished extraction is not repeated.

The demo stops by blocking a handler inside one process, not by killing it.

Each tool call carries the node's idempotency key and the ledger ignores a repeat of a key it has seen. The dispatcher hands a registered handler only its arguments, so the key reaches the handler as the `idempotency_key` argument. A payment is protected only if the ledger you plug in honours that key.

An amount that is not a positive number, or is over the limit in the YAML, goes to `flag_for_review`; it is never paid.

Run without a key, using a scripted model:

```
dotnet run --project samples/Ampelum.Samples.ExpenseApproval -c Release -- --fake
```

Run against a live model: copy `.sample.env` to `.env` and set `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1`, then run the same command without `--fake`.

Tests: `dotnet test tests/Ampelum.Samples.ExpenseApproval.Tests`.

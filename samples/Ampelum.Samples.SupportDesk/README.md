# Support desk

A customer ticket goes in, a checked and audited reply comes out.

The pipeline in `SupportDeskPipeline.cs` runs four steps:

1. Triage with `Predict` and `JsonAdapter`: category, urgency and sentiment.
2. Knowledge lookup with `RagAdvisor` over an `InMemoryRetriever` and a small in-code knowledge base.
3. Order lookup with `ReAct` and a `lookup_order` tool over an in-memory dictionary.
4. Reply with `VerifiedPredict` and `SchemaCriticPolicy`; a rejected draft is retried with the critic's hint.

Every model call passes through `BudgetHook`, `AuditHook`, `PromptInjectionHook` and `ContentPolicyHook`. After the reply, `AuditChainVerifier` checks the audit chain. Services are wired with `AddAmpelum`. A draft blocked by the content policy is not written to the audit log, because the after-hooks stop first.

## Run with a scripted model

```
dotnet run --project samples/Ampelum.Samples.SupportDesk/Ampelum.Samples.SupportDesk.csproj -- --fake
```

No network and no key are needed. The scripted answers live in `FakeScript.cs`.

## Run against a live model

Without `--fake` the sample reads `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1` from `.env` (copy `.sample.env` to `.env` and fill them in) or from environment variables.

## Tests

```
dotnet test tests/Ampelum.Samples.SupportDesk.Tests/Ampelum.Samples.SupportDesk.Tests.csproj
```

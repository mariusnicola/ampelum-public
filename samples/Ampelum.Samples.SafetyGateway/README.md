# Safety gateway

An internal helpdesk assistant whose every model call passes one safety layer. The layer checks who the agent is, keeps the directory credential away from the model and cuts known key shapes out of text, redacts personal data in the audit trail, moderates the reply, decides which tools may run, counts tokens and cost, and writes an audit file that can be verified and replayed.

`SafetyGateway.cs` is the entry point. A run looks like this:

1. `LocalJwtIdentityProvider` issues a signed token with the agent type, an instance id and a scope. The gateway checks it at start and again before every question.
2. The moderation key is read from the secret store (`ChainSecretStore` over `InMemorySecretStore` and `EnvironmentSecretStore`) under the instance id. The directory tool takes a `secret-ref` argument: the model writes an alias, `SecretRefResolver` swaps in the value inside the tool, and the value is cleared afterwards.
3. The question goes through `OutboundSanitizer`. Injection phrases are flagged and left in place; a pasted key in a known shape (AWS, GitHub, `sk-` style, JWT, Slack, private key blocks, `api_key=` style pairs of 20 or more characters) is cut before the model sees it. See the limits below for what is not caught.
4. The model call runs the hooks: `IdentityStampHook` (1), `AuditHook` (10), `PiiRedactionHook` (50), `ModerationHook` (300, fails closed), `CredentialRedactionHook` (400).
5. Tools: `ToolGateHook` checks the registry and the categories the identity scope allows, then `ToolDispatcher` applies `ModeAwareToolPolicy` and the trust policy (`FileTrustStore`, `AgentTrustPolicy`), then the handler runs, then the result goes through the sanitizer before the model reads it. `run_shell` ends in `ProcessSandbox`, which refuses. A tool may only ask for the secret aliases listed for it; any other alias is refused before anything is resolved. A tool error is sanitized before the model or the audit file sees it. The directory tool also replaces the exact credential value it resolved, in what it returns and in what it throws, even as plain text with no key shape; that exact scrub covers only the secret the tool resolved, so any other secret in a tool result depends on the known shapes only. Preflight review applies the same alias rule, so a review and a real run agree.
6. The reply is sanitized again for the caller. `DefaultUsageTracker` and `ModelRateTable` turn the token counts into a cost.
7. `ReviewToolCallsAsync` checks tool calls in OpenAI, Anthropic or Gemini format (`ToolCallFormatConverter`) against the same gate and policy without running anything.

Safety exceptions propagate (`ModerationException`, `UnauthorizedAccessException`) so the caller decides what a blocked reply means. Refusals, tools that ran (tool name and argument names only), blocked replies, flagged input, sanitized tool results and moderation outages are written to the same audit chain as the model calls (`SafetyNotes.cs`), without the text that was removed. Every entry, notes included, carries the run's instance id as its trace id. When the moderation service is unreachable, times out or answers with something unreadable, the reply is withheld (fail closed), a `moderation.unavailable` note is written with the exception type, and the caller gets `ModerationUnavailableException`. A cancellation the caller asked for stays a cancellation and is not noted.

## What PII redaction does

`PiiRedactionHook` changes the audit file only. The prompt sent to the model, the trace record and the reply returned to the caller keep the original text. `PiiTests` asserts both sides: the model receives the email address as typed, the audit file holds `<REDACTED:EMAIL>`. If personal data must not reach the model, this hook does not do that.

## Run with a scripted model

```
dotnet run --project samples/Ampelum.Samples.SafetyGateway/Ampelum.Samples.SafetyGateway.csproj -- --fake
```

No network and no key are needed. The model answers come from `FakeScript.cs`, the moderation service is `FakeModerationHandler` behind an `HttpClient`, and the secrets in `FakeData.cs` are plainly fake. The program prints the answers, the tools that ran, the refusals, the blocked reply, usage and cost, and whether the audit chain is valid. Exit code 0 means the chain is valid.

What `--fake` proves: the wiring. The hooks, the gate, the dispatcher, the secret store, the sanitizer and the audit file work together on scripted input, and the audit chain verifies.

What it does not prove: anything about a real model or a real moderation service. The scripted model asks for the tools you would expect, the fake service flags a marker string, and the directory and handbook are in-memory stand-ins. The live path below has not been run.

## Run against a live model

Without `--fake` the program reads these settings from `.env` (copy `.sample.env` to `.env`) or from environment variables:

| Setting | Meaning |
| --- | --- |
| `OPENROUTER_API_KEY`, `OPENROUTER_MODEL_1` | Chat model, as in the other samples. In `.sample.env`. |
| `MODERATION_ENDPOINT` | URL of an OpenAI style `/moderations` endpoint. Not in `.sample.env`. |
| `MODERATION_API_KEY` | Key for that endpoint. Not in `.sample.env`. |
| `MODEL_INPUT_RATE`, `MODEL_OUTPUT_RATE` | Price per token in USD, for the cost estimate. Not in `.sample.env`. |

The moderation key is stored in the secret store for the run. An `AMPL_SECRET_<alias>` environment variable is the fallback for an alias the run has not stored. The live path has not been run.

## Tests

```
dotnet test tests/Ampelum.Samples.SafetyGateway.Tests/Ampelum.Samples.SafetyGateway.Tests.csproj
```

The tests use `FakeChatClient` and `CapturingTraceSink` from `Ampelum.Testing`, a per-test temp folder, and rates written in the test. They assert what the model received, what was blocked, the audit file content, replay and the cost numbers.

## What the sanitizer does not catch

`OutboundSanitizer` matches known key shapes and generic `name = value` pairs with 20 or more characters. These pass through to the model, the trace and the audit file unchanged (`KnownLimitTests` pins them):

- `password: hunter2hunter2` (value under 20 characters)
- `Authorization: Bearer abcdef1234567890abcdef`
- `postgres://user:pw12345@db.example.test/app`
- a bare 40 character hex string

## Limits found while building this

Each of these was seen by running the code, not only by reading it.

- Replay works from the audit file for `AskAsync`. It cannot serve a question the PII hook changed, because the audit entry is keyed on the redacted text and the lookup uses the original (`AuditReplayMissingException`). A tool run cannot be replayed either: a replayed model turn carries its text only, not its tool calls.
- Audit entries made by `Predict` carry model id `unknown` and zero token counts, because the hook context and the audit hook use different metadata keys. Cost comes from the usage tracker, not from the audit file.
- `BatchedAuditSink` does not dispose the sink it wraps, and `AddBatchedAuditSink<T>` needs an inner sink the container can construct, which `JsonFileAuditSink` is not. The gateway registers both itself.
- `SemanticCacheHook` is not used. On a cache hit the PII hook is skipped, so the audit entry holds the original text.
- `StreamingChatSession` is not used. The before hooks do run, but the text is sent to the caller before the after hooks, and those hooks are given no outputs, so moderation has nothing to check and never calls the service.
- `FileTrustStore` keeps trust levels in memory only; nothing is written to its folder.
- `ProcessSandbox` is not a sandbox. It runs a process with the caller's rights and refuses any policy that asks for isolation, which is how this sample uses it.
- Nothing in the library fills `tools.requested`, so `ToolGateHook` does nothing until the caller publishes the tool names, as `ToolGovernance.CheckGateAsync` does.
- `ModelRateTable` holds one price pair; the model id passed to `EstimateCost` is ignored.
- `EditTool` returns a string and edits nothing. `EncryptedFileSecretStore` writes under the user profile with a key derived from the machine name and has no public constructor for another path, so neither is used.

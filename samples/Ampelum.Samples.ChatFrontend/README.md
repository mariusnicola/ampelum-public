# Chat frontend

The backend of a customer chat widget. A conversation reply streams token by token, the typed fields of an answer stream as they are produced, a repeated question is answered without a model call, a repeated identical prompt never reaches the model twice, and when the primary model fails the fallback model answers.

## What it shows

| Feature | Where it runs |
| --- | --- |
| `StreamingChatSession` streams a reply token by token | `ChatBackend.OpenConversation` |
| `ProgramStreamer.Streamify` with `IStreamableProgram` and `StreamUpdate` streams the typed fields | `ChatBackend.StreamFieldsAsync`, `ReplyProgram.cs` |
| `SemanticCacheHook` with `InMemorySemanticCache`, registered by `AddSemanticCache` | `ChatBackend` constructor, `ChatBackend.AskAsync` |
| `TokenCache` stops a repeated identical prompt | `TokenCachedChatClient.cs`, `ChatBackend.QuickReplyAsync` |
| `RoutedChatClient` with `Failover` | `ChatBackend` constructor |

`PinnedModelClient.cs` sends each endpoint's own model id. `ChatDemo.cs` runs the scenario and `Program.cs` picks fake or live.

## Run with a scripted model

```
dotnet run --project samples/Ampelum.Samples.ChatFrontend/Ampelum.Samples.ChatFrontend.csproj -- --fake
```

No key and no network. The models are `FakeChatClient` queues in `FakeScript.cs`; this sample makes no HTTP call of its own, so there is no scripted HTTP handler. Exit code 0 means every step of the scenario did its job: more than one token arrived, both typed fields arrived and the last update carried the typed value, the repeated question got the same answer and added no cache entry, the repeated prompt got the same text, and the fallback model answered after a scripted primary outage.

What `--fake` proves: the wiring. The five library features cooperate on scripted input, and the model call counts in `tests/Ampelum.Samples.ChatFrontend.Tests` show which calls reached which model.

What it does not prove: anything about a real model, a real provider's streaming format, or how a real outage looks. The semantic cache step in fake mode succeeds because the same scripted question is asked twice.

## Run against a live model

Copy `.sample.env` to `.env`, or use environment variables. Without `--fake` the program needs:

| Setting | Meaning |
| --- | --- |
| `OPENROUTER_API_KEY` | Key for the provider. |
| `OPENROUTER_MODEL_1` | Model id of the primary endpoint. |
| `OPENROUTER_MODEL_2` | Model id of the fallback endpoint. Required; it never falls back to `OPENROUTER_MODEL_1`. |

A missing setting stops the run with its name. Both endpoints use the one client from `SampleConfig.CreateChatClient`, and `PinnedModelClient` sets `ChatOptions.ModelId` per endpoint. That the provider client honours a per-call model id was not checked against the real client. A live model cannot be made to fail on demand, so the live run skips the failover step. The live path has never been run.

## Tests

```
dotnet test tests/Ampelum.Samples.ChatFrontend.Tests/Ampelum.Samples.ChatFrontend.Tests.csproj
```

The tests read no setting and use no network. Each feature has a test that fails when the sample's use of it is broken: `FeatureTests.cs` for the five features, `SettingsAndDemoTests.cs` for the required settings and the fake run, `KnownLimitTests.cs` for the limits below.

## Limits found while building this

Each was seen by running the code. `KnownLimitTests` pins every one except the TokenCache note.

- `SemanticCacheHook` matches by embedding similarity only when an embedding generator is registered. This sample registers none and defines no embedding setting, so it matches identical inputs and a reworded question calls the model again (`SemanticCacheWithoutAnEmbeddingGenerator_MatchesIdenticalInputsOnly...`). The key includes the signature type, so two signatures with the same input names do not share entries (`SemanticCacheKeyIncludesTheSignatureType...`).
- A semantic cache hit stops the before-pass (`src/Ampelum/Hooks/HookPipeline.cs`, line 46). `SemanticCacheHook` is ordered 52, so `PiiRedactionHook` (50) still runs its before step on a hit and the audit entry holds `<REDACTED:EMAIL>`, as the miss does (`SemanticCacheHit_StillRunsThePiiHook_SoTheAuditEntryHoldsTheRedactedInputs`). Hooks ordered above 52, such as the prompt-injection hook (75), do not run their before step on a hit.
- Streaming does not fail over. `RoutedChatClient.GetStreamingResponseAsync` returns the primary's stream without any fallback (`src/Ampelum/Routing/RoutedChatClient.cs`, line 83). A primary outage during a conversation reaches the caller while a one-shot call in the same outage is rescued (`KnownLimit_StreamingDoesNotFailOver...`).
- `StreamingChatSession` yields each token before the after hooks run. It gives them the whole reply as `HookContext.Outputs` (`text`), so `ModerationHook` checks a streamed reply (`Conversation_GivesTheAfterHooksTheWholeReply`), but it can only fail the turn once the stream ends, after the flagged text reached the caller (`KnownLimit_ConversationSendsTextBeforeTheAfterHooks...`). A widget that must not show unsafe text cannot rely on moderation alone with this session.
- Nothing in `src` calls `TokenCache`, so no chat client uses it. `TokenCachedChatClient` is the sample's own wiring. `TokenCache.BuildKey` keys on the model id and each message's role and text only, so two calls that differ in temperature or other options share an entry. In this sample the model id in the key is empty, because `TokenCachedChatClient` sits outside `PinnedModelClient`, which sets the id later.

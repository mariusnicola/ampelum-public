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

- `SemanticCacheHook` is not semantic. It is an exact match on a SHA-256 of the module name and the input values (`src/Ampelum/Caching/Semantic/SemanticCacheKey.cs`, line 26). It takes no embedding generator, so this sample has none and defines no embedding setting. A reworded question calls the model again (`KnownLimit_SemanticCacheMatchesIdenticalInputsOnly...`).
- The semantic cache key ignores the signature type. `Predict` names the module `Predict` for every signature (`src/Ampelum/Predict.cs`, lines 140 and 486), so two signatures with the same input names share entries and the second gets the first one's outputs. This is why the sample uses one signature for the one-shot and the streamed answer.
- A semantic cache hit stops the before-pass (`src/Ampelum/Hooks/HookPipeline.cs`, line 46). A hook with a higher order, such as `PiiRedactionHook` (50), never runs its before step on a hit. In the audit file the miss holds `<REDACTED:EMAIL>` and the hit holds the address as typed (`KnownLimit_SemanticCacheHitStopsTheBeforePass...`). The hook's after step does run on a hit.
- Streaming does not fail over. `RoutedChatClient.GetStreamingResponseAsync` returns the primary's stream without any fallback (`src/Ampelum/Routing/RoutedChatClient.cs`, line 83). A primary outage during a conversation reaches the caller while a one-shot call in the same outage is rescued (`KnownLimit_StreamingDoesNotFailOver...`).
- `StreamingChatSession` yields each token before the after hooks run (`src/Ampelum/StreamingChatSession.cs`, line 204 against line 303) and never sets `HookContext.Outputs`. `ModerationHook` returns at once when outputs are null, so it never checks a streamed reply, while the same hook blocks a typed answer (`KnownLimit_ConversationSendsTextBeforeTheAfterHooks...`, `KnownLimit_ModerationHookDoesWork...`). A widget that must not show unsafe text cannot use this session with moderation as is.
- Nothing in `src` calls `TokenCache`, so no chat client uses it. `TokenCachedChatClient` is the sample's own wiring. `TokenCache.BuildKey` keys on the model id and each message's role and text only, so two calls that differ in temperature or other options share an entry. In this sample the model id in the key is empty, because `TokenCachedChatClient` sits outside `PinnedModelClient`, which sets the id later.

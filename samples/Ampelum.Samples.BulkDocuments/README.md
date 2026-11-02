# Bulk documents

A batch of documents is summarized through a batch client. Single documents are summarized interactively, also as a stream.

`BulkDocumentsPipeline.cs` does two things:

1. Batch: `AnthropicBatchSubmitter` gets all documents in one submit. The pipeline polls the status, reads the results and maps them back to documents by custom id. A document that errored is submitted again alone, up to `BatchAttempts` times, using `RetryModule` and `AmpelumAssert`. A document that never succeeds keeps the provider's error.
2. Interactive: `Predict` with `ForwardAsync` or `StreamAsync` runs through this chat stack, innermost first:
   - `CircuitBreakerChatClient` around each endpoint, so a failing endpoint is skipped.
   - `RoutedChatClient` with `Failover`, `LoadBalanced` or `KeyRotating`, set by `BulkDocumentsOptions.Routing` and wired with `AddProviderEndpoint` and `AddRouting`.
   - `RateLimitedChatClient` through `AddRateLimit`. A call that gets no token within `AcquireTimeout` fails with `RateLimitedException`.
   - `AmpelumCachingChatClient` through `WithResponseCache`, backed by `FileDistributedCache`. It is outermost, so a hit costs no rate limit token and reaches no provider.

An unparseable reply is re-asked with the parse error by `RetryHook`.

The response cache is exact match. The same prompt text and options hit. A document that differs by one character misses. There is no similarity matching.

Streaming does not fail over. The router picks one endpoint per streamed request and does not try the next one.

The demo run keeps its cache in a temporary folder and deletes it at the end, so it shows a hit inside one run only. That the cache survives a new pipeline instance on disk is shown by the test `Cache_OnDisk_SurvivesANewPipelineInstance`.

If a later batch attempt is canceled, expires or never ends, the summaries already received are returned and the remaining documents carry the reason as their error. A failure of the first batch still throws.

## Run with scripted answers

```
dotnet run --project samples/Ampelum.Samples.BulkDocuments/Ampelum.Samples.BulkDocuments.csproj -- --fake
```

No network and no key are needed. The batch endpoints are played by `FakeAnthropicBatchHandler`, the chat endpoints by two `FakeChatClient` instances in `FakeScript.cs`. The exit code is 0 when every check in `Program.cs` holds.

## Run against live models

Without `--fake` the sample reads these settings from `.env` (copy `.sample.env` to `.env` and fill them in) or from environment variables:

- `ANTHROPIC_API_KEY` and `ANTHROPIC_MODEL` for the batch.
- `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1` for the primary interactive endpoint.
- `OPENROUTER_MODEL_2` for the fallback endpoint, same key.

## Tests

```
dotnet test tests/Ampelum.Samples.BulkDocuments.Tests/Ampelum.Samples.BulkDocuments.Tests.csproj
```

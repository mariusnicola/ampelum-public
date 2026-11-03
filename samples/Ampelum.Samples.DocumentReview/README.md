# Document review

A team receives supplier documents and has to decide which clauses matter, which need a human lawyer, and which documents just need a summary by morning. One run does all four jobs, each on one library type:

| Job | File | Library type |
| --- | --- | --- |
| Read the PDFs page by page | `ReviewDesk.cs` (`LoadAsync`) | `PdfLoader` |
| Rank the pages that matter for a question | `ReviewDesk.cs` (`RerankAsync`) | `CohereReranker` or `VoyageReranker`, chosen by a setting |
| Send the clauses the ranking is unsure about to a remote reviewer | `ReviewDesk.cs` (`EscalateAsync`) | `A2AClient`: agent card, task send, result |
| Queue the bulk summaries overnight | `ReviewDesk.cs` (`SummaryQueue`) | `OpenAIBatchSubmitter`: submit, status, results, cancel |

`ReviewRun.cs` runs the steps in that order and decides the exit code.

The sample builds its PDFs at run time with its own small writer (`MiniPdf.cs`) into a folder under the system temp directory and removes it at the end. No binary file is checked in; `LoadTests.NoPdfIsCheckedIn` guards that. One extra file in the folder is not a PDF, to show what `PdfLoader` does with it.

## How a run goes

1. The PDFs are loaded. A file that yields no pages is reported as needing a person.
2. The pages are reranked against the question. A clause scoring under `ReviewDesk.AutoApproveScore` is a hard case. That number is a policy constant, not tuned to any vendor's score scale.
3. Each hard case goes to the remote agent. The sample reads the agent card first and refuses to send anything when the card names a different host than the configured agent URL. The client would post wherever the card says.
4. The overnight summaries go out as one batch. The sample polls the status and waits `ReviewDesk.PollInterval` between polls through the `TimeProvider` it hands to `OpenAIBatchSubmitter`. Then it reads the results. A second batch holds one document the supplier withdrew; it is submitted and cancelled, and the status is read again.

Exit code 0 means every hard case got a completed verdict, the batch completed with one result per document, and the cancelled batch reports cancelled.

## Run with fakes

```
dotnet run --project samples/Ampelum.Samples.DocumentReview/Ampelum.Samples.DocumentReview.csproj -- --fake
```

No key and no network beyond 127.0.0.1 (in practice no socket at all). Three sample-owned `HttpMessageHandler`s play the services: `FakeRerankHandler.cs` (Cohere and Voyage frames, which differ in `top_n` against `top_k` and `results` against `data`), `FakeAgentHandler.cs` (agent card and task send), `FakeBatchHandler.cs` (file upload, batch create, status, output file, cancel). `InstantTimeProvider.cs` is the fake clock: every timer fires at once and each requested delay is recorded. The offline run uses both rerankers; a live run uses the one the setting names.

Tests:

```
dotnet test tests/Ampelum.Samples.DocumentReview.Tests/Ampelum.Samples.DocumentReview.Tests.csproj -c Release --no-build
```

## Run live

Copy `.sample.env` to `.env` and set the names below. Each is required only by the part that uses it, and a missing one stops the run at that part with its name. The parts run in the order load, rerank, agent, batch, so a missing batch setting fails after the first three have run and cost something.

| Name | Used by |
| --- | --- |
| `REVIEW_RERANKER` | rerank: `cohere` or `voyage` |
| `REVIEW_RERANKER_KEY` | rerank: API key of the chosen service |
| `REVIEW_RERANKER_MODEL` | rerank: model id sent to the chosen service |
| `REVIEW_A2A_URL` | agent: base URL of the remote reviewer, `http` or `https` |
| `REVIEW_BATCH_KEY` | batch: API key |
| `REVIEW_BATCH_MODEL` | batch: model id sent with the whole batch |

Nothing has a default and no setting falls back to another. The endpoints are not settings: the live run calls the vendors' public API base URLs, which the library sets as its defaults for the Cohere rerank endpoint, the Voyage rerank endpoint and the OpenAI batch API. Those URLs are neither secrets nor model choices, so the sample leaves them alone. Under `--fake` they are replaced by loopback addresses.

```
dotnet run --project samples/Ampelum.Samples.DocumentReview/Ampelum.Samples.DocumentReview.csproj
```

**The live path has never been run.** No key was available when this was written.

## What `--fake` proves and does not

It proves the sample calls each library type with the right frames: the rerank request carries every page, the key and the limit under the vendor's own field names; the agent receives the clause text and the question; the batch file holds one line per document with the configured model; the poll loop waits through the clock it was given; cancel reaches the batch. Each of these has a test that fails when the call is broken.

It does not prove that a real service accepts those frames or answers like the fakes. The fakes were written from the library's own tests and code, not from a vendor's documentation. The fake scores are the share of query terms found in the text; real scores differ, so the 0.5 threshold means nothing for a real service until someone tunes it. `PollInterval` and the polling limit are plain constants.

## Library defects found

Each of these is pinned by a test in `LibraryDefectTests.cs` unless it says it was read from the code only. A test that fails there means the library changed and the line here must go.

**What the A2A card verifier does not check** (`src/Ampelum.Agent/A2A/A2AIntegration.cs`, `DefaultAgentCardVerifier`):

- `VerifyCapabilities` (line 135 on) always returns an empty list. The comment at line 146 says it would validate against schemas and look for suspicious patterns "in practice"; it does neither. A card whose capabilities carry a custom entry such as an exec request or a wildcard scope gets no warning. A null list and an empty list also get none.
- `VerifyIdentityAsync` (line 119 on) checks the signature and expiry of the token inside the card and nothing else. It does not compare the token's agent type with the card's name, nor tie the token to the card's URL. A valid token issued for a different agent is accepted on any card.
- The scheme is matched case-sensitively as `"bearer"` (line 125). `"Bearer"` returns no identity.
- `VerifyAsync` catches every exception and returns null (line 94 to 97), so a malformed token and a bug look the same.
- `VerifyCapabilitiesAsync` only checks that the identity's scope contains the names asked for. It says nothing about the card.
- This sample does not call the verifier. It compares the card's URL with the configured agent URL instead, which is the one check it needed.

**A2A client** (`src/Ampelum.Agent/A2A/`):

- `A2AClient.FetchAgentCardAsync` does not check that the card's `Url` belongs to the host the card came from (`A2AClient.cs` line 47). Pinned.
- The client serializes enums as numbers (role, task state), and `A2ATask` requires `CreatedAt` and `UpdatedAt` (`A2ATask.cs`). A response with `"state":"completed"` or without timestamps fails with a `JsonException`. The public protocol sends strings, as far as I know; I had no network to check the specification text, so the tests pin only that the client rejects them. Because of this the fake agent sends numbers.
- The method name `tasks/send` and the card path `/.well-known/agent-card` (`A2AMessageRequest.cs` lines 61 and 69) look like an older draft. Not verified against the current specification, so treat as a suspicion.
- `DefaultA2ATaskExecutor` sends its outgoing message with `MessageRole.Agent` (line 183), not the user role. Pinned.
- A JSON-RPC error comes back as a value in `A2AResponse.Error`, not as an exception. Callers have to read it; `DefaultA2ATaskExecutor` does, `A2AClient` does not. The sample reads it (`ReviewDesk.ToVerdict`).
- `A2AClient.Dispose` disposes the `HttpClient` it was given (line 198). Disposing the client while sharing the `HttpClient` breaks the other user. Read from code.
- `ListTasksAsync` puts the page token in `Id` and the status filter in `HistoryLength` (lines 177 and 178). Read from code, not used here.

**`PdfLoader`** (`src/Ampelum.Retrieval/Loaders/PdfLoader.cs`): a file it cannot open, corrupt or not a PDF at all, produces no documents and no error (lines 30 to 32). An empty result cannot tell an empty PDF from a broken one. The sample treats zero pages as a problem and says so in its output.

**Rerankers** (`CohereReranker.cs` and `VoyageReranker.cs`, `src/Ampelum/Retrieval/Hybrid/`):

- An `index` outside the candidate list throws a raw `ArgumentOutOfRangeException` (line 48 in both). Pinned.
- An empty JSON body (`null`) throws `NullReferenceException` (line 45 in both). Pinned.

**`OpenAIBatchSubmitter`** (`src/Ampelum/Batch/OpenAIBatchSubmitter.cs`):

- The `TimeProvider` is used once, for `BatchHandle.SubmittedAt` (line 88). The submitter does not poll or wait, so passing a fake clock does nothing for polling unless the caller's own loop uses it. This sample's loop does, and a test checks the delays.
- `StreamResultsAsync` throws when the batch has no `output_file_id` (line 118), including a cancelled or expired batch that may still have produced partial output on a real service. Read from code; the fake does not model partial output.
- `"cancelling"` is mapped to `Cancelled` (line 186), so a batch that is still stopping reports as stopped.

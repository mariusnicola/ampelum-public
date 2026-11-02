# Research assistant

A question goes through four specialist agents that search a small document set and use tools. A cited answer comes out.

The pipeline in `ResearchAssistantPipeline.cs` runs a `GroupChat` with a `RoleBasedSelector` and an `InMemoryConversationMemory` that the agents read from:

1. Planner (`Predict` with `JsonAdapter`) turns the question into search queries.
2. Researcher searches `KnowledgeBase` for each query.
3. Analyst (`ReAct`) works out the answer with four tools: `CalculatorTool`, `WikipediaTool`, `WebSearchTool` with `BraveSearchProvider`, and `CodeInterpreterTool`.
4. Writer (`Predict`) writes the answer with source ids in square brackets and ends the chat.

`KnowledgeBase.cs` loads the files in `knowledge/` with `MarkdownLoader`, splits them with `RecursiveCharacterSplitter`, and indexes the chunks in `BM25Retriever` and in `EmbeddingRetriever` over an `InMemoryVectorStore`. `HybridRetriever` fuses both with `ReciprocalRankFusionReranker`, then `CrossEncoderReranker` orders the pool with `OverlapScorer`, a plain term-overlap scorer, so no ONNX model is needed. The embedder is a local hashing embedder, so no embedding model or key is needed either.

Afterwards the pipeline checks every cited id. An id must come from a retrieved passage or a tool (`wiki:`, `web:`); the run is grounded only if at least one source is cited and none is unknown. `BudgetHook` caps the model calls.

The code interpreter runs in this process without a sandbox and without a timeout. `CodeGuard` in `ResearchTools.cs` is the only limit: expressions up to 200 characters made of arithmetic and `Math` calls pass, everything else (LINQ, strings, other types, statements) is refused before any code runs. Treat it as a demonstration, not a security boundary.

`OverlapScorer` is a term-overlap stand-in for a real cross-encoder, and `HashingEmbeddingGenerator` is a word-hashing stand-in for a real embedding provider. Both are used in live mode too, so only the chat model and the web search are live.

## Run with a scripted model

```
dotnet run --project samples/Ampelum.Samples.ResearchAssistant/Ampelum.Samples.ResearchAssistant.csproj -- --fake
```

No network and no key are needed. `FakeModel.cs` answers from the messages it is sent, and `FakeWebHandler.cs` plays Wikipedia and Brave Search for the real tool code. The exit code is 0 when the answer is grounded.

## Run against a live model

Without `--fake` the sample reads `OPENROUTER_API_KEY`, `OPENROUTER_MODEL_1` and `BRAVE_API_KEY` from `.env` (copy `.sample.env` to `.env` and fill them in) or from environment variables. Pass your own question as the first argument that does not start with `--`.

## Tests

```
dotnet test tests/Ampelum.Samples.ResearchAssistant.Tests/Ampelum.Samples.ResearchAssistant.Tests.csproj
```

# Vector store swap

The same retrieval code runs over pgvector, Pinecone or Qdrant. A setting picks the store. Under `--fake` the sample shows that the three adapters, and the code above them, give one ranking for one question. It shows nothing about the real databases.

`VectorStoreSwapPipeline.cs` builds the services with `AddAmpelum`, `WithEmbeddingGenerator` and `WithRetriever<EmbeddingRetriever>`. The only store-specific code is `WithConfiguredVectorStore` in `VectorStoreRegistration.cs`, which reads `VECTOR_STORE` and calls `WithVectorStore<T>` for the matching store. Indexing, retrieval and `RagAdvisor` are written against `IVectorStore` and `IRetriever` and never name a store. `InMemoryVectorStore` is accepted as a fourth value, `inmemory`, and serves as the reference ranking.

## Run with fake backends

```
dotnet run --project samples/Ampelum.Samples.VectorStoreSwap/Ampelum.Samples.VectorStoreSwap.csproj -- --fake
```

No key, no network and no container are needed. The three stores get in-memory backends from `FakeBackends.cs` through the test constructors each store provides. The embeddings and the chat answer are scripted. The exit code is 0 when every store returns the same ranking and no compared scores differ, 1 otherwise. All three fake backends share one in-memory cosine ranking, so agreement is expected by construction. The run prints a line saying so.

What this shows: each adapter hands its backend the rows, filter and `topK` that the tests assert, and the code above the adapters behaves the same whichever one sits below. Nothing checks that a real backend accepts those requests. What it does not prove: anything about the real databases. The fake backends rank by cosine similarity, which pgvector (`<=>`) and Qdrant (`Distance.Cosine`) use; a Pinecone index uses whatever metric it was created with. The SQL, the gRPC calls and the REST calls are not exercised.

## Run against real stores

Copy `.sample.env` to `.env` and set `VECTOR_STORE` to `pgvector`, `pinecone` or `qdrant`, then the settings of that store only. The sample indexes the knowledge base in the chosen store and in `InMemoryVectorStore` with the same embeddings, and compares the two rankings. Scores are reported as not compared, because only one service store runs.

Settings read by this sample (see "Settings" below for the ones `.sample.env` does not list yet):

| Setting | Used when |
|---|---|
| `OPENROUTER_API_KEY`, `OPENROUTER_MODEL_1` | always, for the answer |
| `EMBEDDING_ENDPOINT`, `EMBEDDING_API_KEY`, `EMBEDDING_MODEL` | always, any OpenAI-compatible embeddings endpoint |
| `VECTOR_STORE`, `VECTOR_DIMENSION` | always, the dimension must match the embedding model |
| `PGVECTOR_CONNECTION_STRING` | `pgvector` |
| `PINECONE_API_KEY`, `PINECONE_ENVIRONMENT`, `PINECONE_INDEX` | `pinecone` |
| `QDRANT_HOST`, `QDRANT_PORT`, `QDRANT_COLLECTION` | `qdrant` (gRPC port, normally 6334) |

```
dotnet run --project samples/Ampelum.Samples.VectorStoreSwap/Ampelum.Samples.VectorStoreSwap.csproj
```

The whole live path has never been run, against any of the three stores or the embeddings endpoint.

- pgvector needs an existing database where `CREATE EXTENSION vector` is allowed. Use an empty scratch database: the sample creates and fills the table `ampelum_documents`. The schema adds an `ivfflat` index with `lists = 100`; how it behaves on a handful of rows has not been tested live.
- Pinecone: the adapter only opens an index by name (`GetIndex`). It never creates one, so the index must already exist with the right dimension and metric. Pinecone is eventually consistent, so a query right after the upsert can return fewer hits than were stored (Pinecone's documented behaviour, not measured here).
- Qdrant creates the collection with the configured dimension only when it does not exist yet. It does not check the dimension of an existing one.
- A `VECTOR_DIMENSION` that differs from what the embedding model returns is not checked by the sample. What happens then is unmeasured, and `InMemoryVectorStore` raises no error because it compares only the shared length.

## Known limits of the stores

Found by reading the store code, not listed in the repository README.

- The pgvector backend builds SQL by string interpolation in two places: the table name, and the metadata filter key (`metadata ->> '<key>'`). Filter values are parameters. A filter key or table name that comes from user input is an injection risk; only pass names you control (`src/Ampelum.VectorStores.PgVector/NpgsqlPgVectorBackend.cs`, `PgVectorSchema.cs`).
- The Qdrant backend turns an id into a point UUID on write and returns that UUID on read (`src/Ampelum.VectorStores.Qdrant/QdrantGrpcBackend.cs`, `ToPointId`). An id that `Guid.TryParse` accepts comes back in canonical lowercase hyphenated form, so braces, no hyphens or upper case do not survive. Any other id becomes a UUID built from its MD5 hash, and the original id is lost. The fake Qdrant backend copies this mapping, and a test shows a non-UUID id does not come back. The sample's own ids are canonical UUIDs so they agree across stores.
- Metadata is stored as strings in Pinecone and Qdrant and as JSON in pgvector. pgvector returns values as `JsonElement`, the others as `string`, and `InMemoryVectorStore` returns the original objects. Filter values are compared as text. The tests compare metadata as text for that reason.
- `InMemoryVectorStore` leaves `RetrievedDocument.Score` at the value it was indexed with when read through `EmbeddingRetriever`, so the retriever shows no score for it. The sample compares only its ranking.
- `PineconeOptions.Environment` is required by the options type, but the Pinecone adapter does not use it.

## Not included

`PostgresWorkflowPersistence` is not part of this sample. It opens its database connection in the constructor and has no seam, so it cannot run under `--fake`, and a test for it would need a live PostgreSQL.

## Settings

`.sample.env` lists every setting this sample reads.

## Tests

```
dotnet test tests/Ampelum.Samples.VectorStoreSwap.Tests/Ampelum.Samples.VectorStoreSwap.Tests.csproj
```

The tests use the fake backends and settings passed in as a function, so they read no environment variable and need no service. The ranking, score, filter and request assertions run the Ampelum stores and retriever; the comparison, options-from-settings and report tests cover the sample's own code.

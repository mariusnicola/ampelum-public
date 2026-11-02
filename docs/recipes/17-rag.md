# Retrieval-Augmented Generation

`EmbeddingRetriever` glues an `IEmbeddingGenerator<string, Embedding<float>>`
to an `IVectorStore`. The recipe stages a 3-document corpus in
`InMemoryVectorStore`, retrieves the top-2 hits via `FakeEmbeddingGenerator`'s
scripted vectors, then drives a `Predict<MathQa>` chain whose prompt is
augmented with the retrieved context.

```csharp
// ampelum:recipe rag
var corpus = new[]
{
    new RetrievedDocument("d1", "The capital of France is Paris.", 0.0,
        new Dictionary<string, object?>()),
    new RetrievedDocument("d2", "Paris hosts the Eiffel Tower.", 0.0,
        new Dictionary<string, object?>()),
    new RetrievedDocument("d3", "Berlin is the capital of Germany.", 0.0,
        new Dictionary<string, object?>()),
};
var vectors = new ReadOnlyMemory<float>[]
{
    new float[] { 1f, 0f, 0f },
    new float[] { 0.9f, 0.1f, 0f },
    new float[] { 0f, 1f, 0f },
};
var store = new InMemoryVectorStore();
await store.UpsertAsync(corpus, vectors, ct);

var embeddings = new FakeEmbeddingGenerator(
    new Dictionary<string, float[]> { ["What is the capital of France?"] = new[] { 1f, 0f, 0f } },
    fallbackDimensions: 3);
var retriever = new EmbeddingRetriever(embeddings, store);

var hits = await retriever.RetrieveAsync("What is the capital of France?", topK: 2, ct: ct);
hits.Count.ShouldBe(2);
hits[0].Id.ShouldBe("d1");

// Chain the retrieved context into a Predict call.
var context = string.Join("\n", hits.Select((h, i) => $"[{i + 1}] {h.Text}"));
var fake = new FakeChatClient();
fake.EnqueueText("[[ ## Answer ## ]]\nParis\n[[ ## completed ## ]]");
var ampelumCtx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var predict = new Predict<MathQa>(ampelumCtx);
var r = await predict.ForwardAsync(
    new MathQa { Question = $"Use this context:\n{context}\n\nQ: capital of France?" },
    ct: ct);
r.Value.Answer.ShouldBe("Paris");
```

Expected: top-2 retrieval ranks d1 above d3; the chained `Predict<MathQa>`
produces `"Paris"` from the context-augmented prompt.

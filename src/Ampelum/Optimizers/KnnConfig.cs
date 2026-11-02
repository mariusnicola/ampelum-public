using SignalGrid.Ampelum.Retrieval;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Compile-time captured configuration for <see cref="KnnFewShot{TProgram}"/>: the embedding
/// generator, the populated vector store, the neighbour count, and the parallel list of
/// training <see cref="Example"/>s addressed by integer document id.
/// </summary>
internal sealed record KnnConfig(
    IEmbeddingGenerator<string, Embedding<float>> EmbeddingGenerator,
    IVectorStore Store,
    int K,
    IReadOnlyList<Example> AllExamples);

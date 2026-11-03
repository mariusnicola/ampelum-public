using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ampelum.Samples.ComposedPrograms.Style;

/// <summary>
/// Local bag-of-words embedder: each term is hashed into a fixed number of buckets. It needs no model and
/// no network and gives the same vectors on every machine. It matches shared words, not meaning.
/// </summary>
public sealed partial class HashingEmbeddingGenerator(int dimensions = 256) : IEmbeddingGenerator<string, Embedding<float>>
{
    [GeneratedRegex(@"\p{L}[\p{L}\p{N}_]*")]
    private static partial Regex TermPattern();

    public EmbeddingGeneratorMetadata Metadata { get; } = new("hashing", defaultModelId: "bag-of-words");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(v => new Embedding<float>(Embed(v))).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (Match term in TermPattern().Matches(text))
            vector[(int)(Fnv1a(term.Value.ToLowerInvariant()) % (uint)dimensions)] += 1f;
        return vector;
    }

    static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : null;

    public void Dispose()
    {
    }
}

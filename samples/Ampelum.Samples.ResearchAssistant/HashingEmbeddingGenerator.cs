using Microsoft.Extensions.AI;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>
/// Local bag-of-words embedder: each term is hashed into a fixed number of buckets. It needs no model
/// and no network, and it is deterministic, so retrieval gives the same ranking on every machine.
/// </summary>
public sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    readonly int _dimensions;

    public HashingEmbeddingGenerator(int dimensions = 256)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        _dimensions = dimensions;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.Select(v => new Embedding<float>(Embed(v))).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    float[] Embed(string text)
    {
        var vector = new float[_dimensions];
        foreach (var term in TermText.Terms(text))
            vector[(int)(Fnv1a(term) % (uint)_dimensions)] += 1f;
        return vector;
    }

    static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var c in s)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

using Microsoft.Extensions.AI;

namespace Ampelum.Tests.Retrieval.Fixtures;

internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly List<string> _calls = [];
    public IReadOnlyList<string> Calls => _calls;
    public EmbeddingGeneratorMetadata Metadata { get; } = new("fake", null, "fake-model", 4);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var v in values)
        {
            _calls.Add(v);
            var vec = new float[] { v.Length, v.Length > 0 ? v[0] : 0f, 0f, 1f };
            result.Add(new Embedding<float>(vec));
        }
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return Metadata;
        }
        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }
        return null;
    }

    public void Dispose() { }
}

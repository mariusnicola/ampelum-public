using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// An in-memory <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for unit testing.
/// Returns scripted vectors for known inputs and a zero vector of specified dimensions
/// length for unrecognised inputs. No network calls; deterministic.
/// </summary>
public sealed class FakeEmbeddingGenerator
    : IEmbeddingGenerator<string, Embedding<float>>
{
    readonly IReadOnlyDictionary<string, float[]> _scripted;
    readonly int _fallbackDimensions;

    /// <summary>
    /// Constructs a <see cref="FakeEmbeddingGenerator"/>.
    /// </summary>
    /// <param name="scripted">Map from input string to output vector.</param>
    /// <param name="fallbackDimensions">Vector length for inputs not found in <paramref name="scripted"/>.</param>
    public FakeEmbeddingGenerator(
        IReadOnlyDictionary<string, float[]> scripted,
        int fallbackDimensions)
    {
        ArgumentNullException.ThrowIfNull(scripted);
        _scripted = scripted;
        _fallbackDimensions = fallbackDimensions;
    }

    /// <inheritdoc/>
    public EmbeddingGeneratorMetadata Metadata { get; } =
        new(nameof(FakeEmbeddingGenerator));

    /// <inheritdoc/>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = values.Select(v =>
        {
            var vec = _scripted.TryGetValue(v, out var scripted)
                ? scripted
                : new float[_fallbackDimensions];
            return new Embedding<float>(vec);
        }).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

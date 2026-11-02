namespace SignalGrid.Ampelum.Context.Retrieval;

/// <summary>Produces vector embeddings for text — independent of MEAI's IEmbeddingGenerator.</summary>
public interface IEmbedder
{
    /// <summary>Dimensionality of the output vectors.</summary>
    int Dim { get; }
    /// <summary>Semantic version of this embedder (changes when the model/weights change).</summary>
    string Version { get; }
    /// <summary>Embeds a single text into a fixed-dimension vector.</summary>
    ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct);
}

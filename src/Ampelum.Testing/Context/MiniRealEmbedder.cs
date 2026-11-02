using System.Text.RegularExpressions;
using SignalGrid.Ampelum.Context.Retrieval;

namespace SignalGrid.Ampelum.Testing.Context;

/// <summary>Provides semantic similarity for retrieval tests. NOT for production use. 64-dim, token-based.</summary>
public sealed partial class MiniRealEmbedder : IEmbedder
{
    /// <inheritdoc />
    public int Dim => 64;
    /// <inheritdoc />
    public string Version => "mini-v1";

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vec = new float[Dim];
        var tokens = TokenRegex().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length >= 2)
            .ToList();

        foreach (var token in tokens)
        {
            var idx = Math.Abs(token.GetHashCode()) % Dim;
            vec[idx] += 1.0f / tokens.Count;
        }

        // L2 normalize
        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
            for (var i = 0; i < Dim; i++) vec[i] /= norm;
        return ValueTask.FromResult<ReadOnlyMemory<float>>(vec);
    }

    [GeneratedRegex(@"[a-zA-Z]+")]
    private static partial Regex TokenRegex();
}

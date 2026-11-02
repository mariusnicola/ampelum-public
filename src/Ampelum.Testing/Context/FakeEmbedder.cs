using System.Text;
using SignalGrid.Ampelum.Context.Retrieval;

namespace SignalGrid.Ampelum.Testing.Context;

/// <summary>TEST-ONLY. Hash-based deterministic 384-dim embedder. Produces no semantic similarity — use MiniRealEmbedder for retrieval tests.</summary>
public sealed class FakeEmbedder : IEmbedder
{
    /// <inheritdoc />
    public int Dim => 384;
    /// <inheritdoc />
    public string Version => "fake-v1";

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default)
    {
        var hash = GetStableHash(text);
        var vec = new float[Dim];
        for (var i = 0; i < Dim; i++)
            vec[i] = (hash[i % 4] % 1000) / 1000f;
        // L2 normalize
        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
            for (var i = 0; i < Dim; i++) vec[i] /= norm;
        return ValueTask.FromResult<ReadOnlyMemory<float>>(vec);
    }

    private static int[] GetStableHash(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var h = new int[4];
        for (var i = 0; i < bytes.Length; i++)
        {
            h[0] = (h[0] * 31 + bytes[i]) % int.MaxValue;
            h[1] = (h[1] * 17 + bytes[i] * 3) % int.MaxValue;
            h[2] = (h[2] * 13 + bytes[i] * 7) % int.MaxValue;
            h[3] = (h[3] * 11 + bytes[i] * 11) % int.MaxValue;
        }
        return h;
    }
}

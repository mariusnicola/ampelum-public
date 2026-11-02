using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>
/// Cosine similarity between embeddings of the named string output field of <typeparamref name="TSig"/>.
/// Returns <see cref="double"/> in <c>[0,1]</c>: raw cosine when <c>threshold</c> is
/// <c>null</c>; otherwise <c>1.0</c> when cosine â‰¥ threshold else <c>0.0</c>.
/// </summary>
/// <remarks>
/// Embedding generation is awaited via <c>.GetAwaiter().GetResult()</c> because
/// <see cref="IMetric{TSig}.Score"/> is sync (same constraint as <see cref="LLMAsJudgeMetric{TSig}"/>).
/// Drive under <see cref="Evaluate{TSig}"/> which dispatches on the thread pool.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class EmbeddingSimilarity<TSig> : IMetric<TSig>, INamedMetric
    where TSig : ISignature
{
    readonly string _fieldName;
    readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    readonly double? _threshold;

    /// <summary>Initialises the metric.</summary>
    /// <param name="fieldName">Name of the string field on <typeparamref name="TSig"/>.</param>
    /// <param name="embedder">Embedding generator (MEAI).</param>
    /// <param name="threshold">Optional threshold in <c>[0,1]</c>; when set, score is 1.0 iff cosine â‰¥ threshold.</param>
    public EmbeddingSimilarity(
        string fieldName,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        double? threshold = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        ArgumentNullException.ThrowIfNull(embedder);
        if (threshold is { } t && (t < 0.0 || t > 1.0))
            throw new ArgumentOutOfRangeException(nameof(threshold), "threshold must be in [0,1] when supplied.");
        _fieldName = fieldName;
        _embedder = embedder;
        _threshold = threshold;
    }

    /// <summary>Stable identifier of the form <c>embed_sim[Field]</c> or <c>embed_sim[Field,â‰¥T]</c>.</summary>
    public string Name => _threshold is { } t
        ? $"embed_sim[{_fieldName},>={t:0.##}]"
        : $"embed_sim[{_fieldName}]";

    /// <inheritdoc/>
    public double Score(TSig actual, TSig expected, SignatureMetadata signature)
    {
        var prop = typeof(TSig).GetProperty(_fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"Signature '{typeof(TSig).Name}' has no property '{_fieldName}'.", nameof(signature));
        var a = prop.GetValue(actual) as string ?? "";
        var e = prop.GetValue(expected) as string ?? "";
        if (a.Length == 0 && e.Length == 0) return 1.0;
        if (a.Length == 0 || e.Length == 0) return 0.0;

        var embeddings = _embedder.GenerateAsync(new[] { a, e }).GetAwaiter().GetResult();
        var va = embeddings[0].Vector.ToArray();
        var ve = embeddings[1].Vector.ToArray();
        var cos = Cosine(va, ve);
        var clamped = Math.Clamp(cos, 0.0, 1.0);
        return _threshold is { } t ? (clamped >= t ? 1.0 : 0.0) : clamped;
    }

    static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

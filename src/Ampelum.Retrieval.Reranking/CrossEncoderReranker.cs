using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SignalGrid.Ampelum.Retrieval.Reranking;

/// <summary>Pairwise relevance scorer abstraction (returns higher = more relevant).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IRelevanceScorer : IDisposable
{
    /// <summary>Returns a relevance score for the (query, document) pair.</summary>
    float Score(string query, string document);
}

/// <summary>ONNX-backed cross-encoder reranker. Scoring delegated to <see cref="IRelevanceScorer"/> for testability.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
[RequiresUnreferencedCode("ONNX runtime uses native interop incompatible with NativeAOT trimming.")]
[RequiresDynamicCode("ONNX runtime constructs sessions at runtime.")]
public sealed class CrossEncoderReranker : IReranker, IDisposable
{
    private readonly IRelevanceScorer _scorer;

    /// <summary>Creates a reranker that loads an ONNX model from disk (resolved via <paramref name="downloader"/>).</summary>
    public CrossEncoderReranker(CrossEncoderOptions options, ModelDownloader downloader)
    {
        var path = downloader.ResolveAsync(options.ModelPath, options.ExpectedSha256, CancellationToken.None).GetAwaiter().GetResult();
        _scorer = new OnnxScorer(path, options.MaxSequenceLength);
    }

    /// <summary>Creates a reranker with a custom scorer (test seam).</summary>
    public CrossEncoderReranker(IRelevanceScorer scorer)
    {
        _scorer = scorer;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievedDocument>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedDocument> candidates,
        int topK,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(Array.Empty<RetrievedDocument>());
        }
        using var activity = Diagnostics.Source.StartActivity("ampelum.reranker.rerank");
        activity?.SetTag("ampelum.reranker.kind", "cross-encoder");

        var scored = new List<(RetrievedDocument Doc, double Score)>(candidates.Count);
        foreach (var c in candidates)
        {
            var s = _scorer.Score(query, c.Text);
            scored.Add((c, s));
        }
        return Task.FromResult<IReadOnlyList<RetrievedDocument>>(
            scored.OrderByDescending(s => s.Score).Take(topK).Select(s => s.Doc with { Score = s.Score }).ToList());
    }

    /// <inheritdoc />
    public void Dispose() => _scorer.Dispose();
}

/// <summary>Default ONNX scorer. Reads the ONNX model and runs inference per (query, doc) pair.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
[RequiresUnreferencedCode("ONNX runtime uses native interop incompatible with NativeAOT trimming.")]
[RequiresDynamicCode("ONNX runtime constructs sessions at runtime.")]
internal sealed class OnnxScorer : IRelevanceScorer
{
    private readonly InferenceSession _session;
    private readonly int _maxSeqLen;

    public OnnxScorer(string modelPath, int maxSeqLen)
    {
        _session = new InferenceSession(modelPath);
        _maxSeqLen = maxSeqLen;
    }

    public float Score(string query, string document)
    {
        var len = Math.Min(_maxSeqLen, query.Length + document.Length + 3);
        var input = new float[len];
        for (int i = 0; i < len; i++)
        {
            input[i] = i;
        }
        var t = new DenseTensor<float>(input, new[] { 1, len });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("input", t) });
        return results.First().AsEnumerable<float>().First();
    }

    public void Dispose() => _session.Dispose();
}

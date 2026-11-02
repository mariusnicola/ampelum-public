using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>In-process embedding engine using ONNX Runtime (all-MiniLM-L6-v2, 384-dim).
/// Falls back to SHA256 deterministic stub when model is unavailable or useOnnx=false.</summary>
public sealed class EmbeddingEngine : IDisposable
{
    private const int Dimensions = 384;
    private const int MaxLength = 256;
    private const float ScaleFactor = 0.01f;

    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ampelum", "models", "all-MiniLM-L6-v2");

    private static readonly string ModelPath = Path.Combine(ModelDir, "model.onnx");
    private static readonly string TokenizerPath = Path.Combine(ModelDir, "tokenizer.json");

    private static readonly Lazy<InferenceSession?> LazySession =
        new(LoadSession, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<TokenizerCache?> LazyTokenizer =
        new(LoadTokenizer, LazyThreadSafetyMode.ExecutionAndPublication);

    private static volatile bool _modelDownloaded;
    private static volatile bool _downloadStarted;
    private static readonly object DownloadLock = new();
    private static readonly HttpClient Http = new();

    private readonly bool _useOnnx;
    private bool _disposed;

    static EmbeddingEngine()
    {
        if (File.Exists(ModelPath) && File.Exists(TokenizerPath))
        {
            _modelDownloaded = true;
        }
        else if (!_downloadStarted)
        {
            lock (DownloadLock)
            {
                if (!_downloadStarted)
                {
                    _downloadStarted = true;
                    _ = DownloadModelAsync();
                }
            }
        }
    }

    /// <summary>Creates a new EmbeddingEngine.</summary>
    /// <param name="useOnnx">If true (default), uses ONNX Runtime with all-MiniLM-L6-v2.
    /// If false, uses SHA256 stub (deterministic, for unit tests).</param>
    public EmbeddingEngine(bool useOnnx = true)
    {
        _useOnnx = useOnnx;
    }

    /// <summary>True when the ONNX model is loaded and ready.</summary>
    public bool ModelAvailable =>
        _useOnnx &&
        _modelDownloaded &&
        LazySession.Value is not null &&
        LazyTokenizer.Value is not null;

    /// <summary>Embed text into a 384-dimensional vector.
    /// Returns null for null or whitespace input.
    /// Uses ONNX inference when available, otherwise falls back to SHA256 stub.</summary>
    public float[]? Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (_useOnnx && _modelDownloaded)
        {
            try
            {
                return OnnxEmbed(text);
            }
            catch
            {
                // Silently fall back to stub
            }
        }

        return StubEmbed(text);
    }

    /// <summary>Compute cosine similarity between two vectors.
    /// Returns 0 if lengths differ, magnitude is zero, or spans are empty.</summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom < float.Epsilon ? 0 : dot / denom;
    }

    /// <summary>Convert float array to byte array (little-endian IEEE 754).</summary>
    public static byte[] FloatArrayToBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Reconstruct float array from byte array (little-endian IEEE 754).</summary>
    public static float[] BytesToFloatArray(byte[] bytes)
    {
        var values = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    // ── ONNX inference ────────────────────────────────────────────────

    private float[] OnnxEmbed(string text)
    {
        var session = LazySession.Value
            ?? ThrowMissing<InferenceSession>("ONNX session not loaded");
        var tokenizer = LazyTokenizer.Value
            ?? ThrowMissing<TokenizerCache>("Tokenizer not loaded");

        // Tokenize: simple white-space split + vocab lookup from tokenizer.json
        var tokenIds = tokenizer.Encode(text);
        var actualLength = Math.Min(tokenIds.Length, MaxLength);

        // Build input tensors
        var inputIds = new int[MaxLength];
        var attentionMask = new int[MaxLength];
        var tokenTypeIds = new int[MaxLength];

        for (var i = 0; i < actualLength; i++)
        {
            inputIds[i] = tokenIds[i];
            attentionMask[i] = 1;
        }

        // Run inference
        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids",
                new DenseTensor<int>(inputIds, new[] { 1, MaxLength })),
            NamedOnnxValue.CreateFromTensor("attention_mask",
                new DenseTensor<int>(attentionMask, new[] { 1, MaxLength })),
            NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<int>(tokenTypeIds, new[] { 1, MaxLength })),
        });

        // Output shape: [1, seq_len, hidden_dim] = [1, MaxLength, 384]
        var output = results[0].AsTensor<float>();

        // Mean pooling over non-padding tokens
        var embedding = new float[Dimensions];
        var validCount = 0;
        var outputSpan = output.ToArray();

        for (var i = 0; i < actualLength; i++)
        {
            if (attentionMask[i] == 1)
            {
                var offset = i * Dimensions;
                for (var j = 0; j < Dimensions; j++)
                    embedding[j] += outputSpan[offset + j];
                validCount++;
            }
        }

        if (validCount > 0)
        {
            var invCount = 1f / validCount;
            for (var i = 0; i < Dimensions; i++)
                embedding[i] *= invCount;
        }

        // L2 normalize
        var norm = 0f;
        for (var i = 0; i < Dimensions; i++)
            norm += embedding[i] * embedding[i];
        norm = MathF.Sqrt(norm);

        if (norm > float.Epsilon)
        {
            var invNorm = 1f / norm;
            for (var i = 0; i < Dimensions; i++)
                embedding[i] *= invNorm;
        }

        return embedding;
    }

    // ── SHA256 stub (deterministic, for tests) ────────────────────────

    private static float[] StubEmbed(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var result = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
            result[i] = (hash[i % 32] - 128) * ScaleFactor;
        return result;
    }

    // ── Model loading ─────────────────────────────────────────────────

    private static InferenceSession? LoadSession()
    {
        try
        {
            return File.Exists(ModelPath)
                ? new InferenceSession(ModelPath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static TokenizerCache? LoadTokenizer()
    {
        try
        {
            if (!File.Exists(TokenizerPath)) return null;
            return TokenizerCache.Load(TokenizerPath);
        }
        catch
        {
            return null;
        }
    }

    // ── Model download (fire-and-forget on first use) ─────────────────

    private static async Task DownloadModelAsync()
    {
        try
        {
            Directory.CreateDirectory(ModelDir);

            // Download model (~23 MB)
            using var modelResp = await Http.GetAsync(
                "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx",
                HttpCompletionOption.ResponseHeadersRead);
            modelResp.EnsureSuccessStatusCode();
            await using var modelStream = await modelResp.Content.ReadAsStreamAsync();
            await using var modelFile = File.Create(ModelPath);
            await modelStream.CopyToAsync(modelFile);

            // Download tokenizer
            using var tokResp = await Http.GetAsync(
                "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/tokenizer.json",
                HttpCompletionOption.ResponseHeadersRead);
            tokResp.EnsureSuccessStatusCode();
            await using var tokStream = await tokResp.Content.ReadAsStreamAsync();
            await using var tokFile = File.Create(TokenizerPath);
            await tokStream.CopyToAsync(tokFile);

            _modelDownloaded = true;
        }
        catch
        {
            // Silently fail — engine will use stub
            _modelDownloaded = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T ThrowMissing<T>(string message) =>
        throw new InvalidOperationException(message);
}

/// <summary>Lightweight tokenizer that loads a HuggingFace tokenizer.json vocab
/// and splits text on whitespace/punctuation for simple BERT-compatible encoding.</summary>
internal sealed class TokenizerCache
{
    private readonly Dictionary<string, int> _vocab;

    private TokenizerCache(Dictionary<string, int> vocab) => _vocab = vocab;

    /// <summary>Load tokenizer from a HuggingFace tokenizer.json file.</summary>
    public static TokenizerCache Load(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var vocab = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Try model.vocab (standard HuggingFace format)
        if (doc.RootElement.TryGetProperty("model", out var model) &&
            model.TryGetProperty("vocab", out var v))
        {
            foreach (var kv in v.EnumerateObject())
                vocab[kv.Name] = kv.Value.GetInt32();
        }

        return new TokenizerCache(vocab);
    }

    /// <summary>Encode text into token IDs using simple whitespace split + vocab lookup.
    /// Unknown tokens mapped to [UNK] (id 100) or 0.</summary>
    public int[] Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        // Simple pre-tokenization: split on whitespace and punctuation
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                if (!char.IsWhiteSpace(ch)) tokens.Add(ch.ToString());
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());

        var ids = new int[tokens.Count + 2]; // +2 for [CLS] and [SEP]
        ids[0] = 101; // [CLS] token for BERT

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].ToLowerInvariant();
            if (_vocab.TryGetValue(token, out var id))
                ids[i + 1] = id;
            else
                ids[i + 1] = 100; // [UNK] token
        }

        ids[^1] = 102; // [SEP] token for BERT
        return ids;
    }
}

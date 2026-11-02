using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace SignalGrid.Ampelum.Retrieval.Caching;

/// <summary>
/// Decorates an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> with a distributed cache
/// keyed on <c>XxHash64(text + "|" + embeddingModelId)</c>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class EmbeddingCache(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    IDistributedCache cache,
    EmbeddingCacheOptions options) : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>Metadata of the wrapped generator (resolved via <c>GetService(typeof(EmbeddingGeneratorMetadata))</c>).</summary>
    public EmbeddingGeneratorMetadata Metadata =>
        inner.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata
        ?? new EmbeddingGeneratorMetadata("unknown");

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? generationOptions = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        var keys = inputs.Select(Key).ToList();
        var resolved = new Embedding<float>?[inputs.Count];
        var missIndices = new List<int>();
        var missTexts = new List<string>();

        for (int i = 0; i < inputs.Count; i++)
        {
            var bytes = await cache.GetAsync(keys[i], cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
            {
                resolved[i] = Decode(bytes);
            }
            else
            {
                missIndices.Add(i);
                missTexts.Add(inputs[i]);
            }
        }

        if (missTexts.Count > 0)
        {
            var generated = await inner.GenerateAsync(missTexts, generationOptions, cancellationToken).ConfigureAwait(false);
            for (int j = 0; j < missIndices.Count; j++)
            {
                resolved[missIndices[j]] = generated[j];
                var entryOptions = new DistributedCacheEntryOptions();
                if (options.TimeToLive != TimeSpan.MaxValue)
                {
                    entryOptions.AbsoluteExpirationRelativeToNow = options.TimeToLive;
                }
                await cache.SetAsync(keys[missIndices[j]], Encode(generated[j]), entryOptions, cancellationToken).ConfigureAwait(false);
            }
        }

        var output = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var e in resolved)
        {
            output.Add(e!);
        }
        return output;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();

    private string Key(string text)
    {
        Span<byte> hashBytes = stackalloc byte[8];
        var input = Encoding.UTF8.GetBytes(text + "|" + Metadata.DefaultModelId);
        XxHash64.Hash(input, hashBytes);
        return $"{options.KeyPrefix}{Convert.ToHexString(hashBytes)}:{Metadata.DefaultModelId}";
    }

    private static byte[] Encode(Embedding<float> e)
    {
        var span = e.Vector.Span;
        var buf = new byte[4 + (span.Length * 4)];
        BinaryPrimitives.WriteInt32LittleEndian(buf, span.Length);
        for (int i = 0; i < span.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4 + (i * 4)), span[i]);
        }
        return buf;
    }

    private static Embedding<float> Decode(byte[] buf)
    {
        var len = BinaryPrimitives.ReadInt32LittleEndian(buf);
        var arr = new float[len];
        for (int i = 0; i < len; i++)
        {
            arr[i] = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(4 + (i * 4)));
        }
        return new Embedding<float>(arr);
    }
}

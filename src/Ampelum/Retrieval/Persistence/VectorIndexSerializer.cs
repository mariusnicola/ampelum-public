using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace SignalGrid.Ampelum.Retrieval.Persistence;

/// <summary>Binary AMV1 serializer for <see cref="InMemoryVectorStore"/> snapshots.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class VectorIndexSerializer
{
    private const uint Magic = 0x414D5631; // 'AMV1' (LE)

    /// <summary>Writes the store's snapshot to <paramref name="destination"/>.</summary>
    public static async Task SaveAsync(IVectorStore store, Stream destination, CancellationToken ct = default)
    {
        if (store is not InMemoryVectorStore mem)
        {
            throw new NotSupportedException("VectorIndexSerializer currently supports InMemoryVectorStore only.");
        }
        var snapshot = mem.Snapshot();
        using (var bw = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Magic);
            bw.Write(snapshot.Documents.Count);
            for (int i = 0; i < snapshot.Documents.Count; i++)
            {
                var d = snapshot.Documents[i];
                bw.Write(d.Id);
                bw.Write(d.Text);
                var metaJson = JsonSerializer.Serialize(d.Metadata);
                bw.Write(metaJson);
                var vec = snapshot.Vectors[i];
                bw.Write(vec.Length);
                foreach (var f in vec)
                {
                    bw.Write(f);
                }
            }
        }
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads an AMV1 snapshot from <paramref name="source"/> and upserts into <paramref name="store"/>.</summary>
    public static async Task LoadAsync(IVectorStore store, Stream source, CancellationToken ct = default)
    {
        if (store is not InMemoryVectorStore mem)
        {
            throw new NotSupportedException("VectorIndexSerializer currently supports InMemoryVectorStore only.");
        }
        using var br = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        if (br.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Bad magic for AMV1 snapshot.");
        }
        int count = br.ReadInt32();
        var docs = new List<RetrievedDocument>(count);
        var vecs = new ReadOnlyMemory<float>[count];
        for (int i = 0; i < count; i++)
        {
            var id = br.ReadString();
            var text = br.ReadString();
            var metaJson = br.ReadString();
            var meta = (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metaJson) ?? new())
                .ToDictionary(pair => pair.Key, pair => PlainValue(pair.Value));
            var len = br.ReadInt32();
            var vec = new float[len];
            for (int j = 0; j < len; j++)
            {
                vec[j] = br.ReadSingle();
            }
            docs.Add(new RetrievedDocument(id, text, 0, meta));
            vecs[i] = vec;
        }
        await mem.UpsertAsync(docs, vecs, ct).ConfigureAwait(false);
    }

    // A string, a bool and null come back plain. A number, array or object stays a JsonElement: it keeps the exact
    // number text, so a save after a load writes the same bytes (converting to int, long or double would not keep a
    // ulong, a decimal's trailing zero or a whole double's ".0"). The store's filter compares numbers by value.
    static object? PlainValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.Clone(),
    };
}

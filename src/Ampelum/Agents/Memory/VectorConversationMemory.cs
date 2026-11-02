using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Retrieval;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Agents.Memory;

/// <summary>
/// Long-term conversation memory backed by an <see cref="IVectorStore"/> and an embedding generator.
/// <see cref="QueryAsync"/> performs k-NN over message embeddings; <see cref="GetRecentAsync"/>
/// uses an in-memory timestamp index since the underlying vector store does not order by recency.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class VectorConversationMemory : IConversationMemory
{
    readonly IVectorStore _store;
    readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    readonly string _collection;
    readonly List<AgentMessage> _timestampIndex = [];
    readonly object _lock = new();
    long _seq;

    /// <summary>Initialises a new vector-backed memory.</summary>
    public VectorConversationMemory(
        IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string collection = "conv-memory")
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(collection);
        _store = store;
        _embedder = embedder;
        _collection = collection;
    }

    /// <inheritdoc/>
    public async ValueTask AppendAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var embeddings = await _embedder
            .GenerateAsync([message.Content], options: null, cancellationToken)
            .ConfigureAwait(false);
        var vec = embeddings[0].Vector;
        long id;
        lock (_lock)
        {
            id = _seq++;
            _timestampIndex.Add(message);
        }
        var json = JsonSerializer.Serialize(message);
        var doc = new RetrievedDocument(
            Id: $"{_collection}:{id}",
            Text: message.Content,
            Score: 0,
            Metadata: new Dictionary<string, object?>
            {
                ["sender"] = message.SenderName,
                ["role"] = message.Role,
                ["timestamp"] = message.Timestamp.ToString("O"),
                ["json"] = json,
                ["collection"] = _collection,
            });
        await _store.UpsertAsync([doc], [vec], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AgentMessage>> GetRecentAsync(int n, CancellationToken cancellationToken)
    {
        if (n <= 0)
            return ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
        AgentMessage[] snapshot;
        lock (_lock) snapshot = [.. _timestampIndex];
        var take = Math.Min(n, snapshot.Length);
        var result = new AgentMessage[take];
        Array.Copy(snapshot, snapshot.Length - take, result, 0, take);
        return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(result);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<AgentMessage>> QueryAsync(string query, int topK, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (topK <= 0) return [];
        var embeddings = await _embedder
            .GenerateAsync([query], options: null, cancellationToken)
            .ConfigureAwait(false);
        var qvec = embeddings[0].Vector;
        var hits = await _store.QueryAsync(
            qvec,
            topK,
            filters: new Dictionary<string, object?> { ["collection"] = _collection },
            ct: cancellationToken).ConfigureAwait(false);
        var result = new List<AgentMessage>(hits.Count);
        foreach (var (doc, _) in hits)
        {
            if (doc.Metadata.TryGetValue("json", out var jsonObj) && jsonObj is string jsonStr)
            {
                var msg = JsonSerializer.Deserialize<AgentMessage>(jsonStr);
                if (msg is not null) result.Add(msg);
            }
        }
        return result;
    }
}

using SignalGrid.Ampelum.Retrieval;

namespace Ampelum.Samples.VectorStoreSwap;

public static class SwapReport
{
    /// <summary>Indexes the knowledge base in every pipeline, asks the same question of each and prints the comparison.</summary>
    /// <returns>0 when every store returned the same ranking and no compared scores differ, otherwise 1.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<VectorStoreSwapPipeline> pipelines, TextWriter output, bool fakeBackends = false, int topK = 3)
    {
        foreach (var pipeline in pipelines)
            await pipeline.IndexAsync(VectorStoreSwapData.Documents);

        var report = await StoreComparison.RunAsync(pipelines, VectorStoreSwapData.SampleQuestion, topK);
        output.WriteLine($"Question: {VectorStoreSwapData.SampleQuestion}");
        foreach (var result in report.Results)
            output.WriteLine($"{result.Store,-9} {string.Join("  ", result.Hits.Select(h => Describe(result.Store, h)))}");
        output.WriteLine($"Same ranking in every store: {report.RankingsMatch}");
        output.WriteLine(report.ScoresMatch is { } scoresMatch
            ? $"Same scores across the service adapters: {scoresMatch}"
            : "Scores across the service adapters: not compared, fewer than two service stores ran");
        if (fakeBackends)
            output.WriteLine("Note: pgvector, pinecone and qdrant run on one in-memory fake here, so agreement shows the adapters and the code above them, not the databases.");

        var answer = await pipelines[0].AskAsync(VectorStoreSwapData.SampleQuestion, topK);
        output.WriteLine($"Answer ({pipelines[0].Store}): {answer.Answer}");
        output.WriteLine($"Sources: {string.Join(", ", answer.Citations.Select(d => d.Id))}");
        return report.RankingsMatch && report.ScoresMatch != false ? 0 : 1;
    }

    // InMemoryVectorStore leaves the document score unset when queried through a retriever, so none is shown.
    static string Describe(string store, RetrievedDocument hit) =>
        store == VectorStoreKinds.InMemory ? hit.Id[^4..] : $"{hit.Id[^4..]}={hit.Score:F4}";
}

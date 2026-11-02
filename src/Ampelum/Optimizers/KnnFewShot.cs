using System.Diagnostics;
using SignalGrid.Ampelum.Retrieval;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// K-Nearest-Neighbour few-shot optimizer. At compile time, embeds every training example
/// using <c>embeddingGenerator</c> and stores the vectors in an in-memory index. At runtime,
/// <see cref="KnnBoundProgram{TProgram}.ResolveNeighboursAsync"/> retrieves the top-k examples
/// matching a query string, suitable for use as <see cref="AmpelumContext.DefaultDemos"/>.
///
/// Standalone (not <see cref="IOptimizer{TProgram}"/>) because output element type
/// (<see cref="KnnBoundProgram{TProgram}"/>) differs from input <typeparamref name="TProgram"/>.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class KnnFewShot<TProgram>
    where TProgram : class
{
    readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    readonly int _k;

    /// <summary>Number of neighbours per query.</summary>
    public int K => _k;

    /// <summary>Construct a KNN few-shot optimizer.</summary>
    public KnnFewShot(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int k = 3)
    {
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        _embeddingGenerator = embeddingGenerator;
        _k = k;
    }

    /// <summary>Compile the program by embedding the training set into a vector store.</summary>
    public async Task<OptimizedProgram<KnnBoundProgram<TProgram>>> CompileAsync(
        TProgram program,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext context,
        IProgress<OptimizerProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(trainset);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.optimizer.compile");
        activity?.SetTag("optimizer.type", nameof(KnnFewShot<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        var store = new InMemoryVectorStore();
        var examples = trainset.ToList();

        if (examples.Count > 0)
        {
            var texts = examples.Select(BuildQueryText).ToList();
            var embeddings = await _embeddingGenerator.GenerateAsync(
                texts, cancellationToken: ct).ConfigureAwait(false);

            var docs = new List<RetrievedDocument>(examples.Count);
            var vectors = new ReadOnlyMemory<float>[examples.Count];
            for (int i = 0; i < examples.Count; i++)
            {
                docs.Add(new RetrievedDocument(
                    Id: i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Text: texts[i],
                    Score: 0.0,
                    Metadata: new Dictionary<string, object?>()));
                vectors[i] = embeddings[i].Vector;
            }
            await store.UpsertAsync(docs, vectors, ct).ConfigureAwait(false);
        }

        progress?.Report(new OptimizerProgress(
            Phase: "indexed", Step: 1, TotalSteps: 1, BestScore: 0.0));

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(KnnFewShot<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(KnnFewShot<TProgram>)));

        var config = new KnnConfig(_embeddingGenerator, store, _k, examples);
        return new OptimizedProgram<KnnBoundProgram<TProgram>>(
            Program: new KnnBoundProgram<TProgram>(program, config),
            DemosBySubmodule: new Dictionary<string, IReadOnlyList<Example>>(),
            InstructionOverrides: new Dictionary<string, string>())
        {
            TrainsetSize = trainset.Count,
        };
    }

    internal static string BuildQueryText(Example ex) =>
        string.Join(" ", ex.Inputs.Values.Select(v => v?.ToString() ?? ""));
}

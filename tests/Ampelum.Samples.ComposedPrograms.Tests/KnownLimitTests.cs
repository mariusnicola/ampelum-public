using Ampelum.Samples.ComposedPrograms.Fake;
using Ampelum.Samples.ComposedPrograms.Style;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Orchestration;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Filtering;
using SignalGrid.Ampelum.Retrieval.Persistence;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>
/// Flaws of the library as observed. Each test asserts the flawed behaviour, so it fails the day the
/// library is fixed and the workaround in the sample can then be removed.
/// </summary>
public sealed class KnownLimitTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Counter : IModule<TightenSig, TightenSig>
    {
        public int Runs { get; private set; }

        public Task<Prediction<TightenSig>> ForwardAsync(TightenSig inputs, CancellationToken ct = default)
        {
            Runs++;
            return Task.FromResult(new Prediction<TightenSig>(inputs, Trace()));
        }
    }

    sealed class StopOnCheck(int stopAt) : IModule<TightenSig, LoopPredicateResult<TightenSig>>
    {
        int _checks;

        public Task<Prediction<LoopPredicateResult<TightenSig>>> ForwardAsync(TightenSig inputs, CancellationToken ct = default)
        {
            var stop = ++_checks >= stopAt;
            return Task.FromResult(new Prediction<LoopPredicateResult<TightenSig>>(
                new LoopPredicateResult<TightenSig>(!stop, stop ? "predicate said stop" : null), Trace()));
        }
    }

    static TightenSig Start() => new() { Title = "t", Draft = "d", Kind = "Fix", Impact = 1, MaxWords = 12 };

    static TraceRecord Trace() =>
        new(Guid.NewGuid(), "test", typeof(TightenSig), new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            [], "", null, TimeSpan.Zero, null, new Dictionary<string, object?>());

    [Fact]
    public async Task Loop_iterations_count_body_runs_not_predicate_checks()
    {
        var body = new Counter();
        var loop = new Loop<TightenSig>(new StopOnCheck(stopAt: 3), body, maxIterations: 10);

        var result = (await loop.ForwardAsync(Start(), Ct)).Value;

        body.Runs.ShouldBe(2);
        result.Iterations.ShouldBe(2);
    }

    [Fact]
    public async Task Loop_keeps_the_predicates_reason_when_it_stops_on_the_check_after_the_last_allowed_run()
    {
        var body = new Counter();
        var loop = new Loop<TightenSig>(new StopOnCheck(stopAt: 3), body, maxIterations: 2);

        var result = (await loop.ForwardAsync(Start(), Ct)).Value;

        body.Runs.ShouldBe(2);
        result.Reason.ShouldBe("predicate said stop");
    }

    [Fact]
    public async Task EmbeddingRetriever_keeps_the_stores_score_so_a_score_floor_keeps_a_close_match()
    {
        var embedder = new HashingEmbeddingGenerator();
        var store = new InMemoryVectorStore();
        var docs = new List<RetrievedDocument> { new("a", "Fixed a login redirect loop", 0, new Dictionary<string, object?>()) };
        var vectors = (await embedder.GenerateAsync(docs.Select(d => d.Text), cancellationToken: Ct)).Select(v => v.Vector).ToArray();
        await store.UpsertAsync(docs, vectors, Ct);

        var plain = await new EmbeddingRetriever(embedder, store).RetrieveAsync("Fixed a login redirect loop", 1, ct: Ct);
        var floored = await new ScoreThresholdRetriever(new EmbeddingRetriever(embedder, store), 0.6)
            .RetrieveAsync("Fixed a login redirect loop", 1, ct: Ct);

        plain.Single().Score.ShouldBe(1.0, 1e-6);
        floored.Single().Id.ShouldBe("a");
    }

    [Fact]
    public async Task A_reloaded_index_still_matches_a_store_side_metadata_filter()
    {
        var embedder = new HashingEmbeddingGenerator();
        var store = new InMemoryVectorStore();
        var docs = new List<RetrievedDocument>
        {
            new("a", "Fixed a login redirect loop", 0, new Dictionary<string, object?> { ["source"] = "history" }),
        };
        var vectors = (await embedder.GenerateAsync(docs.Select(d => d.Text), cancellationToken: Ct)).Select(v => v.Vector).ToArray();
        await store.UpsertAsync(docs, vectors, Ct);
        var filter = new Dictionary<string, object?> { ["source"] = "history" };
        var query = (await embedder.GenerateAsync(["Fixed a login redirect loop"], cancellationToken: Ct)).Single().Vector;

        using var buffer = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(store, buffer, Ct);
        buffer.Position = 0;
        var restored = new InMemoryVectorStore();
        await VectorIndexSerializer.LoadAsync(restored, buffer, Ct);

        (await store.QueryAsync(query, 1, filter, Ct)).Count.ShouldBe(1);
        (await restored.QueryAsync(query, 1, filter, Ct)).Count.ShouldBe(1);
    }
}

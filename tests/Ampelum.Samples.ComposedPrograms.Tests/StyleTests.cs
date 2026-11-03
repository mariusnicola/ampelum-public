using Ampelum.Samples.ComposedPrograms.Fake;
using Ampelum.Samples.ComposedPrograms.Style;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Caching;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>The style index and the ways its retriever can be asked.</summary>
public sealed class StyleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<ChangeRow> Fix() => await Helpers.RowAsync("C-102");

    static async Task<(string Notes, ScriptedModel Model)> NotesAsync(RetrievalMode mode, ChangeRow? row = null)
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(
            model, Rig.Source(), Rig.Options(o => o with { StyleRetrieval = mode }));
        return (await pipeline.StyleNotesAsync(row ?? await Fix(), Ct), model);
    }

    [Fact]
    public async Task The_index_holds_style_chunks_and_past_note_sections_but_no_script_text()
    {
        var embedder = new HashingEmbeddingGenerator();

        var index = await StyleIndex.BuildAsync(Rig.DataDirectory, embedder, 120, Ct);

        var ids = index.Documents.Select(d => d.Id).ToList();
        ids.ShouldContain("style:0");
        ids.ShouldContain("history:Orbit 2.3 > Fixes");
        ids.ShouldContain("history:Orbit 2.2 > Fixes");
        index.Documents.ShouldNotContain(d => d.Text.Contains("must not reach the index"));
        index.Documents.ShouldNotContain(d => d.Text.Contains("<p>"));
    }

    [Fact]
    public async Task A_direct_search_finds_the_fix_section_for_a_fix()
    {
        var (notes, model) = await NotesAsync(RetrievalMode.Direct);

        notes.ShouldContain("Fixed a login redirect loop");
        model.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_score_floor_removes_text_that_is_not_about_the_change()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(
            model, Rig.Source(), Rig.Options(o => o with { StyleMinScore = 0.99 }));

        var notes = await pipeline.StyleNotesAsync(await Fix(), Ct);

        notes.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_notes_are_cut_to_the_token_budget()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(
            model, Rig.Source(), Rig.Options(o => o with { StyleTopK = 4, StyleTokenBudget = 10 }));

        var notes = await pipeline.StyleNotesAsync(await Fix(), Ct);

        notes.ShouldNotBeEmpty();
        notes.Length.ShouldBeLessThan(80);
    }

    [Theory]
    [InlineData(RetrievalMode.Rewrite, "Rewrite the change as one past tense release note sentence")]
    [InlineData(RetrievalMode.Hyde, "hypothetical answer")]
    [InlineData(RetrievalMode.MultiQuery, "paraphrases")]
    [InlineData(RetrievalMode.StepBack, "more abstract")]
    [InlineData(RetrievalMode.SubQuestion, "Decompose")]
    [InlineData(RetrievalMode.Routed, "Reply with one word")]
    public async Task Each_pattern_mode_makes_its_own_model_call_before_it_searches(RetrievalMode mode, string phrase)
    {
        var (notes, model) = await NotesAsync(mode);

        model.Count(phrase).ShouldBeGreaterThan(0);
        notes.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Rewrite_searches_with_the_rewritten_sentence_so_the_notes_change()
    {
        var (direct, _) = await NotesAsync(RetrievalMode.Direct, await Helpers.RowAsync("C-105"));
        var (rewritten, _) = await NotesAsync(RetrievalMode.Rewrite, await Helpers.RowAsync("C-105"));

        rewritten.ShouldNotBe(direct);
    }

    [Fact]
    public async Task The_router_sends_a_wording_question_to_the_style_page_only()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(
            model, Rig.Source(), Rig.Options(o => o with { StyleRetrieval = RetrievalMode.Routed }));
        var wording = new ChangeRow("X-1", "Docs", "Wording: start every note with a past tense verb such as Added, Fixed or Removed",
            "Name the change a user can see, not the code that changed.");

        var notes = await pipeline.StyleNotesAsync(wording, Ct);

        notes.ShouldContain("past tense verb");
        notes.ShouldNotContain("Orbit");
        notes.ShouldNotContain("- Fixed");
    }

    [Fact]
    public async Task The_router_sends_a_question_about_earlier_notes_to_the_history_only()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(
            model, Rig.Source(), Rig.Options(o => o with { StyleRetrieval = RetrievalMode.Routed }));
        var history = new ChangeRow("X-2", "Docs", "Earlier notes: Fixed a login redirect loop after a session expired",
            "Fixed invoice totals that ignored discounts.");

        var notes = await pipeline.StyleNotesAsync(history, Ct);

        notes.ShouldContain("- Fixed");
        notes.ShouldNotContain("past tense verb");
    }

    [Fact]
    public async Task A_route_keeps_the_other_source_out_even_when_the_query_text_matches_it()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(
            model, Rig.Source(), Rig.Options(o => o with { StyleRetrieval = RetrievalMode.Routed }));
        var styleRouteHistoryText = new ChangeRow("X-3", "Docs", "Wording: Fixed a login redirect loop after a session expired",
            "Fixed invoice totals that ignored discounts.");
        var historyRouteStyleText = new ChangeRow("X-4", "Docs",
            "Earlier notes: start every note with a past tense verb such as Added, Fixed or Removed",
            "Name the change a user can see, not the code that changed.");

        var styleOnly = await pipeline.StyleNotesAsync(styleRouteHistoryText, Ct);
        var historyOnly = await pipeline.StyleNotesAsync(historyRouteStyleText, Ct);

        styleOnly.ShouldNotContain("- Fixed");
        historyOnly.ShouldNotContain("past tense verb");
    }

    [Fact]
    public async Task A_saved_index_is_written_once_and_a_second_pipeline_restores_it_without_rebuilding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"style-{Guid.NewGuid():N}.json");
        try
        {
            var model = ReleaseScript.Create();
            var trip = await StyleDemo.RoundTripAsync(model, Rig.Source(), await Fix(), Rig.Options(), path);

            trip.FileWritten.ShouldBeTrue();
            trip.Built.ShouldNotBeEmpty();
            trip.Restored.ShouldBe(trip.Built);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_restored_index_comes_from_the_file_not_from_the_data_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"style-{Guid.NewGuid():N}.json");
        var empty = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        try
        {
            var model = ReleaseScript.Create();
            await using (var first = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options(o => o with { StyleIndexPath = path })))
                await first.StyleNotesAsync(await Fix(), Ct);

            await using var second = new ReleaseNotesPipeline(
                model, new ChangeSource(empty), Rig.Options(o => o with { StyleIndexPath = path }));
            var notes = await second.StyleNotesAsync(await Fix(), Ct);

            notes.ShouldContain("Fixed a login redirect loop");
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(empty, true);
        }
    }

    sealed class CountingEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        readonly HashingEmbeddingGenerator _inner = new();
        int _texts;

        public int Texts => Volatile.Read(ref _texts);

        public EmbeddingGeneratorMetadata Metadata => _inner.Metadata;

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Interlocked.Add(ref _texts, list.Count);
            return await _inner.GenerateAsync(list, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_text_that_is_searched_twice_is_embedded_once_by_the_pipeline_cache()
    {
        var counting = new CountingEmbedder();
        await using var pipeline = new ReleaseNotesPipeline(
            ReleaseScript.Create(), Rig.Source(), Rig.Options(), embeddings: counting);
        var row = await Fix();

        await pipeline.StyleNotesAsync(row, Ct);
        var afterFirst = counting.Texts;
        await pipeline.StyleNotesAsync(row, Ct);

        // The first search embedded the index and the query; the second asked for the same query again.
        afterFirst.ShouldBeGreaterThan(5);
        counting.Texts.ShouldBe(afterFirst);
    }

    [Fact]
    public async Task A_routed_search_still_filters_after_the_index_was_saved_and_restored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"style-{Guid.NewGuid():N}.json");
        try
        {
            var options = Rig.Options(o => o with { StyleRetrieval = RetrievalMode.Routed, StyleIndexPath = path });
            var model = ReleaseScript.Create();
            await using (var builder = new ReleaseNotesPipeline(model, Rig.Source(), options))
                await builder.StyleNotesAsync(await Fix(), Ct);
            await using var restored = new ReleaseNotesPipeline(model, Rig.Source(), options);
            var wording = new ChangeRow("X-1", "Docs", "Wording: start every note with a past tense verb such as Added, Fixed or Removed",
                "Name the change a user can see, not the code that changed.");
            var history = new ChangeRow("X-2", "Docs", "Earlier notes: Fixed a login redirect loop after a session expired",
                "Fixed invoice totals that ignored discounts.");

            var style = await restored.StyleNotesAsync(wording, Ct);
            var past = await restored.StyleNotesAsync(history, Ct);

            // Restored metadata comes back as JSON values; the filter must still read them.
            style.ShouldContain("past tense verb");
            past.ShouldContain("- Fixed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Chunk_ids_stay_unique_when_two_sections_have_the_same_heading_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"long-section-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(Rig.DataDirectory, StyleIndex.StyleGuideFile), Path.Combine(dir, StyleIndex.StyleGuideFile));
            // The splitter makes one chunk per heading, so a repeated heading repeats the path.
            File.WriteAllText(Path.Combine(dir, StyleIndex.PastReleasesFile),
                "# Orbit 9\n\n## Fixes\n\n- Fixed the first problem.\n\n## Fixes\n\n- Fixed the second problem.\n");

            var index = await StyleIndex.BuildAsync(dir, new HashingEmbeddingGenerator(), 120, Ct);

            var ids = index.Documents.Where(d => d.Id.StartsWith("history:")).Select(d => d.Id).ToList();
            ids.Count.ShouldBe(2);
            ids.Distinct().Count().ShouldBe(ids.Count);
            ids.ShouldContain("history:Orbit 9 > Fixes");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task All_modes_are_run_by_the_demo()
    {
        var model = ReleaseScript.Create();

        var notes = await StyleDemo.ModesAsync(model, Rig.Source(), await Fix(), Rig.Options());

        notes.Keys.ShouldBe(Enum.GetValues<RetrievalMode>(), ignoreOrder: true);
    }

    [Fact]
    public async Task The_retrieval_harness_reports_rank_metrics_over_the_labeled_queries()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());

        var result = await pipeline.EvaluateRetrievalAsync(2, Ct);

        result.Examples.ShouldBe(6);
        result.Means["mrr"].ShouldBe(1.0, 0.001);
        result.Means["recall@2"].ShouldBe(1.0, 0.001);
        result.Means["precision@2"].ShouldBe(0.5, 0.001);
    }

    [Fact]
    public async Task A_wrong_gold_id_lowers_the_harness_scores()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "retrieval-eval.csv"), "query,gold_ids\nFixed a login redirect loop,style:99\n");
            var model = ReleaseScript.Create();
            var source = new ChangeSource(Rig.DataDirectory);
            await using var pipeline = new ReleaseNotesPipeline(model, source, Rig.Options());
            var memory = await pipeline.StyleAsync();

            var result = await memory.EvaluateAsync(Path.Combine(dir, "retrieval-eval.csv"), 2, Ct);

            result.Means["mrr"].ShouldBe(0.0, 0.001);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

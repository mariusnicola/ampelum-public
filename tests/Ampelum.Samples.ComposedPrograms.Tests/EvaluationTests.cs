using System.Text.Json;
using Ampelum.Samples.ComposedPrograms.Evaluation;
using Ampelum.Samples.ComposedPrograms.Fake;
using Ampelum.Samples.ComposedPrograms.Style;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>The CSV files, the shipped metrics scored by name and the evaluation report.</summary>
public sealed class EvaluationTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly SignatureMetadata Draft = SignatureMetadataCache.For<DraftSig>();
    static readonly SignatureMetadata Classify = SignatureMetadataCache.For<ClassifySig>();

    static DraftSig Note(string text) => new() { Title = "t", Details = "d", Note = text };

    static ClassifySig Label(string kind, int impact) => new() { Title = "t", Details = "d", Kind = kind, Impact = impact };

    static async Task<(ReleaseNotesPipeline Pipeline, ScriptedModel Model)> OpenAsync()
    {
        var model = ReleaseScript.Create();
        return (new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options()), model);
    }

    [Fact]
    public async Task The_csv_files_load_every_row_with_their_columns()
    {
        var changes = await Rig.Source().LoadChangesAsync(Ct);
        var approved = await Rig.Source().LoadApprovedAsync(Ct);

        changes.Count.ShouldBe(8);
        changes[2].ShouldBe(new ChangeRow("C-103", "Search", "Remove legacy search API v1",
            "The v1 search endpoint is gone. Clients must call v2 search with the new query parameter."));
        approved.Count.ShouldBe(10);
        approved[2].Expected.Note.ShouldBe("Removed XML import. Use JSON import instead.");
        approved[2].Inputs.Impact.ShouldBe(4);
    }

    [Fact]
    public void Exact_match_compares_the_field_as_written()
    {
        var metric = new ExactMatch<ClassifySig>(nameof(ClassifySig.Kind));

        metric.Score(Label("Fix", 1), Label("Fix", 1), Classify).ShouldBe(1.0);
        metric.Score(Label("fix", 1), Label("Fix", 1), Classify).ShouldBe(0.0);
    }

    [Fact]
    public void Case_insensitive_match_ignores_case()
    {
        var metric = new CaseInsensitiveMatch<ClassifySig>(nameof(ClassifySig.Kind));

        metric.Score(Label("fix", 1), Label("Fix", 1), Classify).ShouldBe(1.0);
        metric.Score(Label("feature", 1), Label("Fix", 1), Classify).ShouldBe(0.0);
    }

    [Fact]
    public void Numeric_tolerance_accepts_a_difference_up_to_the_tolerance()
    {
        var metric = new NumericWithTolerance<ClassifySig>(nameof(ClassifySig.Impact), 1);

        metric.Score(Label("Fix", 3), Label("Fix", 4), Classify).ShouldBe(1.0);
        metric.Score(Label("Fix", 3), Label("Fix", 5), Classify).ShouldBe(0.0);
    }

    [Fact]
    public void F1_rouge_and_bleu_score_word_overlap_with_the_shipped_formulas()
    {
        var f1 = new F1<DraftSig>(nameof(DraftSig.Note));
        var rouge1 = new RougeN<DraftSig>(1, nameof(DraftSig.Note));
        var rougeL = new RougeL<DraftSig>(nameof(DraftSig.Note));
        var bleu = new Bleu<DraftSig>(2, nameof(DraftSig.Note));

        f1.Score(Note("the cat sat"), Note("the cat ran"), Draft).ShouldBe(2.0 / 3.0, 0.001);
        rouge1.Score(Note("the cat sat"), Note("the cat ran"), Draft).ShouldBe(2.0 / 3.0, 0.001);
        rougeL.Score(Note("the cat sat"), Note("the cat ran"), Draft).ShouldBe(2.0 / 3.0, 0.001);
        bleu.Score(Note("the cat sat down"), Note("the cat sat down"), Draft).ShouldBe(1.0, 0.001);
        // Shared-nothing text is smoothed above zero by the shipped formula, but stays far below a match.
        bleu.Score(Note("one two three"), Note("four five six"), Draft).ShouldBeLessThan(0.5);
    }

    [Fact]
    public void A_user_defined_metric_runs_the_function_it_was_given()
    {
        var metric = new UserDefined<DraftSig>((actual, _) => WordCount.Of(actual.Note) <= 3 ? 1.0 : 0.0);

        metric.Score(Note("short note here"), Note(""), Draft).ShouldBe(1.0);
        metric.Score(Note("a much longer note than allowed"), Note(""), Draft).ShouldBe(0.0);
    }

    [Fact]
    public void Embedding_similarity_is_one_for_equal_text_and_lower_for_other_text()
    {
        var metric = new EmbeddingSimilarity<DraftSig>(nameof(DraftSig.Note), new HashingEmbeddingGenerator());

        var same = metric.Score(Note("Fixed a login loop"), Note("Fixed a login loop"), Draft);
        var other = metric.Score(Note("Fixed a login loop"), Note("Added team workspaces"), Draft);

        same.ShouldBe(1.0, 0.001);
        other.ShouldBeLessThan(same);
    }

    [Fact]
    public async Task Every_metric_name_resolves_to_the_metric_class_registered_for_it()
    {
        var (pipeline, _) = await OpenAsync();
        await using var _ = pipeline;
        var registry = pipeline.Services.GetRequiredService<IMetricRegistry>();

        registry.Get(MetricNames.KindExact).ShouldBeOfType<ExactMatch<ClassifySig>>();
        registry.Get(MetricNames.KindIgnoreCase).ShouldBeOfType<CaseInsensitiveMatch<ClassifySig>>();
        registry.Get(MetricNames.ImpactWithinOne).ShouldBeOfType<NumericWithTolerance<ClassifySig>>();
        registry.Get(MetricNames.NoteF1).ShouldBeOfType<F1<DraftSig>>();
        registry.Get(MetricNames.NoteBleu2).ShouldBeOfType<Bleu<DraftSig>>();
        registry.Get(MetricNames.NoteRouge1).ShouldBeOfType<RougeN<DraftSig>>();
        registry.Get(MetricNames.NoteRougeL).ShouldBeOfType<RougeL<DraftSig>>();
        registry.Get(MetricNames.NoteEmbedding).ShouldBeOfType<EmbeddingSimilarity<DraftSig>>();
        registry.Get(MetricNames.NoteJudge).ShouldBeOfType<LLMAsJudgeMetric<DraftSig>>();
        registry.Get(MetricNames.NoteFits).ShouldBeOfType<UserDefined<DraftSig>>();
    }

    [Fact]
    public async Task The_registered_length_and_rouge_metrics_use_the_options_and_their_configured_order()
    {
        var (pipeline, _) = await OpenAsync();
        await using var _ = pipeline;
        var registry = pipeline.Services.GetRequiredService<IMetricRegistry>();
        var fits = (IMetric<DraftSig>)registry.Get(MetricNames.NoteFits);
        var rouge1 = (IMetric<DraftSig>)registry.Get(MetricNames.NoteRouge1);

        fits.Score(Note(Words(12)), Note(""), Draft).ShouldBe(1.0);
        fits.Score(Note(Words(13)), Note(""), Draft).ShouldBe(0.0);
        // Unigram recall of "the cat sat" against "the cat ran"; a bigram metric would give 0.5.
        rouge1.Score(Note("the cat sat"), Note("the cat ran"), Draft).ShouldBe(2.0 / 3.0, 0.001);
    }

    static string Words(int count) => string.Join(' ', Enumerable.Range(1, count).Select(i => $"w{i}"));

    [Fact]
    public async Task The_judge_metric_asks_the_model_and_reads_back_its_score()
    {
        var (pipeline, model) = await OpenAsync();
        await using var _ = pipeline;
        var judge = (IMetric<DraftSig>)pipeline.Services.GetRequiredService<IMetricRegistry>().Get(MetricNames.NoteJudge);

        var same = judge.Score(Note("Fixed a login loop"), Note("Fixed a login loop"), Draft);
        var other = judge.Score(Note("Improved the product"), Note("Fixed a login loop"), Draft);

        same.ShouldBe(1.0, 0.001);
        other.ShouldBe(0.0, 0.001);
        model.Count("You grade release notes").ShouldBe(2);
        model.Calls.First().User.ShouldContain("Fixed a login loop");
    }

    [Fact]
    public async Task The_classifier_is_scored_on_the_approved_notes_with_the_metrics_named_in_the_options()
    {
        var (pipeline, model) = await OpenAsync();
        await using var _ = pipeline;

        var summary = await pipeline.EvaluateClassificationAsync(Ct);

        summary.Scores.Select(s => s.Metric).ShouldBe(MetricNames.Classify);
        model.Count("Classify one product change").ShouldBe(10 * MetricNames.Classify.Count);
        summary.Mean(MetricNames.KindIgnoreCase).ShouldBe(1.0, 0.001);
        summary.Mean(MetricNames.KindExact).ShouldBe(0.8, 0.001);
        summary.Mean(MetricNames.ImpactWithinOne).ShouldBe(0.8, 0.001);
    }

    [Fact]
    public async Task The_impact_tolerance_comes_from_the_options()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options(o => o with { ImpactTolerance = 5 }));

        var summary = await pipeline.EvaluateClassificationAsync(Ct);

        summary.Mean(MetricNames.ImpactWithinOne).ShouldBe(1.0, 0.001);
    }

    [Fact]
    public async Task A_metric_list_in_the_options_limits_what_is_reported_and_asked()
    {
        var model = ReleaseScript.Create();
        var options = Rig.Options(o => o with { DraftMetrics = [MetricNames.NoteF1] });
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), options);

        var summary = await new ReleaseEvaluation(pipeline.Services.GetRequiredService<IMetricRegistry>(), Rig.Source())
            .DraftAsync((sig, token) => new Predict<DraftSig>(Rig.Context(model)).ForwardAsync(sig, ct: token), options.DraftMetrics, Ct);

        summary.Scores.Select(s => s.Metric).ShouldBe([MetricNames.NoteF1]);
        model.Count("You grade release notes").ShouldBe(0);
    }

    [Fact]
    public async Task The_draft_scores_are_computed_from_the_shipped_metrics_over_ten_examples()
    {
        var (pipeline, _) = await OpenAsync();
        await using var _ = pipeline;

        var summary = await pipeline.EvaluateDraftingAsync(Ct);

        summary.Scores.Select(s => s.Metric).ShouldBe(MetricNames.Draft);
        summary.Scores.ShouldAllBe(s => s.Failures == 0);
        summary.Mean(MetricNames.NoteFits).ShouldBe(1.0, 0.001);
        summary.Mean(MetricNames.NoteRougeL).ShouldBe(0.326, 0.001);
        summary.Mean(MetricNames.NoteF1).ShouldBe(0.337, 0.001);
    }

    [Fact]
    public async Task The_report_has_a_table_per_metric_and_a_json_document_that_parses()
    {
        var (pipeline, _) = await OpenAsync();
        await using var _ = pipeline;

        var summary = await pipeline.EvaluateClassificationAsync(Ct);

        summary.Markdown.ShouldContain("## kind.exact");
        summary.Markdown.ShouldContain("| Examples | 10 |");
        using var json = JsonDocument.Parse(summary.Json);
        json.RootElement.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task A_program_that_throws_is_counted_as_failures_in_the_report()
    {
        var (pipeline, _) = await OpenAsync();
        await using var _ = pipeline;
        var evaluation = new ReleaseEvaluation(pipeline.Services.GetRequiredService<IMetricRegistry>(), Rig.Source());

        var summary = await evaluation.DraftAsync(
            (_, _) => throw new InvalidOperationException("model down"), [MetricNames.NoteF1], Ct);

        summary.Scores.Single().Failures.ShouldBe(10);
        summary.Markdown.ShouldContain("| Failures | 10 |");
    }
}

using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Metrics;

namespace Ampelum.Samples.ComposedPrograms.Evaluation;

/// <summary>Registry names of the metrics the evaluation can report.</summary>
public static class MetricNames
{
    public const string KindExact = "kind.exact";
    public const string KindIgnoreCase = "kind.ignore-case";
    public const string ImpactWithinOne = "impact.within-one";
    public const string NoteF1 = "note.f1";
    public const string NoteBleu2 = "note.bleu-2";
    public const string NoteRouge1 = "note.rouge-1";
    public const string NoteRougeL = "note.rouge-l";
    public const string NoteEmbedding = "note.embedding";
    public const string NoteJudge = "note.judge";
    public const string NoteFits = "note.fits";

    public static IReadOnlyList<string> Classify { get; } = [KindExact, KindIgnoreCase, ImpactWithinOne];

    public static IReadOnlyList<string> Draft { get; } =
        [NoteF1, NoteBleu2, NoteRouge1, NoteRougeL, NoteEmbedding, NoteJudge, NoteFits];
}

/// <summary>Puts every shipped metric the sample reports into one named registry.</summary>
public static class MetricCatalog
{
    /// <summary>Instructions of the judge. A judge is a model call, so it is the only metric that costs money.</summary>
    public const string JudgeInstructions =
        "You grade release notes. Compare the actual note with the expected note. Reward the same change in plain, short words.";

    public static AmpelumBuilder AddReleaseMetrics(
        this AmpelumBuilder builder,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IChatClient judge,
        ReleaseNotesOptions options) =>
        builder.AddMetricRegistry(registry => registry
            .Add(MetricNames.KindExact, new ExactMatch<ClassifySig>(nameof(ClassifySig.Kind)))
            .Add(MetricNames.KindIgnoreCase, new CaseInsensitiveMatch<ClassifySig>(nameof(ClassifySig.Kind)))
            .Add(MetricNames.ImpactWithinOne, new NumericWithTolerance<ClassifySig>(nameof(ClassifySig.Impact), options.ImpactTolerance))
            .Add(MetricNames.NoteF1, new F1<DraftSig>(nameof(DraftSig.Note)))
            .Add(MetricNames.NoteBleu2, new Bleu<DraftSig>(2, nameof(DraftSig.Note)))
            .Add(MetricNames.NoteRouge1, new RougeN<DraftSig>(1, nameof(DraftSig.Note)))
            .Add(MetricNames.NoteRougeL, new RougeL<DraftSig>(nameof(DraftSig.Note)))
            .Add(MetricNames.NoteEmbedding, new EmbeddingSimilarity<DraftSig>(nameof(DraftSig.Note), embedder))
            .Add(MetricNames.NoteJudge, new LLMAsJudgeMetric<DraftSig>(judge, JudgeInstructions))
            .Add(MetricNames.NoteFits, new UserDefined<DraftSig>(
                (actual, _) => WordCount.Of(actual.Note) <= options.MaxWords ? 1.0 : 0.0)));
}

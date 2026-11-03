using Ampelum.Samples.ComposedPrograms.Evaluation;
using Ampelum.Samples.ComposedPrograms.Style;

namespace Ampelum.Samples.ComposedPrograms;

/// <summary>Tunable limits of the release notes pipeline. Models and keys are never part of it.</summary>
public sealed record ReleaseNotesOptions
{
    /// <summary>Longest note the tightening loop accepts.</summary>
    public int MaxWords { get; init; } = 12;

    /// <summary>A stalled note is accepted when it is at most this many times MaxWords.</summary>
    public double ToleranceFactor { get; init; } = 1.5;

    /// <summary>A note whose word count changed by less than this between two passes counts as stalled.</summary>
    public decimal StalledEpsilon { get; init; } = 1m;

    /// <summary>How far the classified impact may differ from the approved one and still count as right.</summary>
    public double ImpactTolerance { get; init; } = 1;

    /// <summary>Most reasoning and tool steps of one fact check.</summary>
    public int FactCheckIterations { get; init; } = 3;

    /// <summary>Most tightening passes before the loop gives up.</summary>
    public int TightenPasses { get; init; } = 3;

    /// <summary>Changes with at least this impact get a composed strategy; the rest get one plain call.</summary>
    public int ComposedImpact { get; init; } = 3;

    /// <summary>Which strategy each kind of change gets when it passes the impact bar.</summary>
    public IReadOnlyDictionary<ChangeKind, DraftStrategy> Strategies { get; init; } =
        new Dictionary<ChangeKind, DraftStrategy>
        {
            [ChangeKind.Feature] = DraftStrategy.MultiChainComparison,
            [ChangeKind.Fix] = DraftStrategy.BestOfN,
            [ChangeKind.Breaking] = DraftStrategy.Audiences,
            [ChangeKind.Internal] = DraftStrategy.Refine,
        };

    public int Candidates { get; init; } = 3;
    public double RefineThreshold { get; init; } = 0.8;
    public int MaxRefinements { get; init; } = 2;

    /// <summary>Instructions that make one drafting module write for one audience.</summary>
    public IReadOnlyList<string> Audiences { get; init; } =
        ["Audience: developers who call the API.", "Audience: administrators who run the product.", "Audience: end users."];

    public string Tone { get; init; } = "plain, past tense, no marketing words";

    /// <summary>Extra columns for the release overview; they are named here and need no code.</summary>
    public IReadOnlyList<string> TagFields { get; init; } = ["Component", "Audience"];

    public int DraftRetries { get; init; } = 1;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Style snippets fetched for each draft.</summary>
    public int StyleTopK { get; init; } = 2;

    public RetrievalMode StyleRetrieval { get; init; } = RetrievalMode.Direct;

    /// <summary>Lowest store score a snippet may have. The store maps cosine to 0..1, so unrelated text scores 0.5.</summary>
    public double StyleMinScore { get; init; } = 0.6;

    public int StyleChunkChars { get; init; } = 120;

    /// <summary>Most tokens of style notes placed in one prompt.</summary>
    public int StyleTokenBudget { get; init; } = 60;

    /// <summary>Where the style index is saved and restored; null keeps it in memory only.</summary>
    public string? StyleIndexPath { get; init; }

    public int PanelMaxTurns { get; init; } = 4;
    public int PanelMemoryCapacity { get; init; } = 20;
    public int EpisodicThreshold { get; init; } = 6;
    public int EpisodicWindow { get; init; } = 3;
    public bool PanelPicksSpeakerWithModel { get; init; }

    /// <summary>Approved notes shown to the drafter as examples by the nearest-neighbour tuner.</summary>
    public int KnnNeighbours { get; init; } = 3;

    /// <summary>Queue trace records so a slow target cannot slow a model call.</summary>
    public bool QueuedTracing { get; init; } = true;

    /// <summary>File that receives one line per trace record; null writes none.</summary>
    public string? TracePath { get; init; }

    /// <summary>Metrics (by registry name) that the evaluation reports.</summary>
    public IReadOnlyList<string> DraftMetrics { get; init; } = MetricNames.Draft;
    public IReadOnlyList<string> ClassifyMetrics { get; init; } = MetricNames.Classify;
}

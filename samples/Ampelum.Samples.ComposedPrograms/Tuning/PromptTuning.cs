using Ampelum.Samples.ComposedPrograms.Drafting;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.ComposedPrograms.Tuning;

/// <summary>Three ways to improve the drafting call from the approved notes of the previous release.</summary>
public sealed class PromptTuning(
    AmpelumContext context,
    IChatClient proposer,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    ReleaseNotesOptions options)
{
    readonly NoteMatch _metric = new();

    /// <summary>
    /// The library defaults, except that every minibatch is the whole trainset (there are only ten approved
    /// notes), a merge is tried every second iteration, and the call budget fits ten such iterations.
    /// </summary>
    public static GepaOptions DefaultOptions { get; } = new() { MinibatchSize = 10, MergeEvery = 2, MaxMetricCalls = 400 };

    public static IReadOnlyList<Example> ToExamples(IReadOnlyList<LabeledExample<DraftSig>> labeled) =>
        labeled.Select(e => Example.From(e.Expected)).ToList();

    public DraftProgram Baseline() => DraftProgram.Unbound(context);

    /// <summary>One drafting call per kind of change: the program GEPA tunes.</summary>
    public KindRoutedProgram GepaBaseline() => KindRoutedProgram.Unbound(context);

    /// <summary>
    /// GEPA runs the program on approved notes, shows the proposer what came out and what the approved note
    /// says, and keeps the instructions that score best on held-out approved notes. The result holds the
    /// instructions as overrides: bind it to get the tuned program.
    /// </summary>
    public async Task<OptimizedProgram<KindRoutedProgram>> GepaAsync(
        IReadOnlyList<Example> trainset, GepaOptions gepa, CancellationToken ct = default,
        IProgress<OptimizerProgress>? progress = null) =>
        await new GEPAOptimizer<KindRoutedProgram>(RunAsync, proposer, gepa, valset: trainset)
            .CompileAsync(GepaBaseline(), trainset, _metric, context, progress, ct).ConfigureAwait(false);

    static async Task<Example> RunAsync(
        KindRoutedProgram program, Example example, AmpelumContext runContext, CancellationToken ct)
    {
        var prediction = await program.ForwardAsync(AsInput(example), ct).ConfigureAwait(false);
        return Example.From(prediction.Value);
    }

    static DraftSig AsInput(Example example) => new()
    {
        Title = Text(example.Inputs, nameof(DraftSig.Title)),
        Details = Text(example.Inputs, nameof(DraftSig.Details)),
        Kind = Text(example.Inputs, nameof(DraftSig.Kind)),
        Impact = int.Parse(Text(example.Inputs, nameof(DraftSig.Impact)), System.Globalization.CultureInfo.InvariantCulture),
        StyleNotes = Text(example.Inputs, nameof(DraftSig.StyleNotes)),
        Tone = Text(example.Inputs, nameof(DraftSig.Tone)),
    };

    /// <summary>K-nearest-neighbour few-shot: each draft gets the k approved notes whose changes read most alike.</summary>
    public async Task<KnnDrafter> KnnAsync(IReadOnlyList<Example> trainset, CancellationToken ct = default)
    {
        var optimized = await new KnnFewShot<DraftProgram>(embedder, options.KnnNeighbours)
            .CompileAsync(Baseline(), trainset, _metric, context, ct: ct).ConfigureAwait(false);
        return new KnnDrafter(optimized.Program);
    }

    /// <summary>Several programs answer and a reducer keeps the draft that the quality metric likes best.</summary>
    public async Task<EnsembleDrafter> EnsembleAsync(IReadOnlyList<DraftProgram> members, CancellationToken ct = default)
    {
        var quality = new NoteQuality(options.MaxWords);
        var meta = SignatureMetadataCache.For<DraftSig>();
        double Quality(Example candidate)
        {
            var draft = AsDraft(candidate);
            return quality.Score(draft, draft with { Note = "" }, meta);
        }

        var ensemble = await new Ensemble<DraftProgram>((candidates, _) => candidates.OrderByDescending(Quality).First())
            .CompileAsync(members, context, ct).ConfigureAwait(false);
        return new EnsembleDrafter(ensemble.Program, context);
    }

    static DraftSig AsDraft(Example example) => new()
    {
        Title = Text(example.Inputs, nameof(DraftSig.Title)),
        Details = Text(example.Inputs, nameof(DraftSig.Details)),
        Note = Text(example.Outputs, nameof(DraftSig.Note)),
    };

    static string Text(IReadOnlyDictionary<string, object?> values, string key) =>
        Convert.ToString(values[key], System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>A program bound to the k nearest approved notes.</summary>
public sealed class KnnDrafter(KnnBoundProgram<DraftProgram> bound)
{
    public int K => bound.K;

    public async Task<(Prediction<DraftSig> Prediction, IReadOnlyList<Example> Neighbours)> DraftAsync(
        DraftSig input, CancellationToken ct = default)
    {
        var neighbours = await bound.ResolveNeighboursAsync($"{input.Title} {input.Details} {input.Kind}", ct).ConfigureAwait(false);
        var prediction = await bound.Inner.Drafter.ForwardAsync(input, neighbours, ct).ConfigureAwait(false);
        return (prediction, neighbours);
    }
}

/// <summary>Runs every member program and lets the reducer choose.</summary>
public sealed class EnsembleDrafter(EnsembleProgram<DraftProgram> program, AmpelumContext context)
{
    public int Members => program.Members.Count;

    public async Task<string> DraftAsync(DraftSig input, CancellationToken ct = default)
    {
        var drafts = new List<Example>();
        foreach (var member in program.Members)
        {
            var prediction = await member.ForwardAsync(input, ct).ConfigureAwait(false);
            drafts.Add(Example.From(prediction.Value));
        }
        var chosen = program.Reducer(drafts, context);
        return Convert.ToString(chosen.Outputs[nameof(DraftSig.Note)], System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}

using Microsoft.Extensions.Logging;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;

namespace Ampelum.Samples.ComposedPrograms.Drafting;

/// <summary>Source of the style snippets that guide one draft.</summary>
public interface IStyleNotes
{
    Task<string> NotesForAsync(string title, string details, CancellationToken ct);
}

/// <summary>What a draft run decided on the way, kept for callers and tests.</summary>
public sealed class DraftRoute
{
    public string Name { get; set; } = "";
    public DraftStrategy? Strategy { get; set; }
    public int TightenRuns { get; set; }
}

/// <summary>
/// Five stages in a Sequence: classify, draft (plain or composed, chosen by a ConditionalBranch),
/// tighten in a Loop, then score.
/// </summary>
public sealed class NoteDrafter
{
    public const string PlainRoute = "plain";
    public const string ComposedRoute = "composed";

    readonly AmpelumContext _ctx;
    readonly ReleaseNotesOptions _options;
    readonly IStyleNotes _style;
    readonly DraftingModules _drafting;
    readonly TightenStage _tighten;

    public NoteDrafter(AmpelumContext ctx, ReleaseNotesOptions options, IStyleNotes style, ILogger logger)
    {
        _ctx = ctx;
        _options = options;
        _style = style;
        _drafting = new DraftingModules(ctx, options, logger);
        _tighten = new TightenStage(ctx, options);
    }

    public DraftingModules Drafting => _drafting;

    public async Task<(DraftedNote Note, DraftRoute Route)> DraftAsync(ChangeRow row, CancellationToken ct = default)
    {
        var route = new DraftRoute();
        var sequence = new Sequence<ClassifySig, ClassifySig, DraftSig, LoopResult<TightenSig>, DraftedNote>(
            ClassifyStage(),
            DraftStage(route),
            TightenModule(route),
            ScoreStage(row, route));
        var prediction = await sequence.ForwardAsync(new ClassifySig { Title = row.Title, Details = row.Details }, ct)
            .ConfigureAwait(false);
        return (prediction.Value, route);
    }

    /// <summary>The classifier asks for XML, which suits a model that is weak at JSON.</summary>
    IModule<ClassifySig, ClassifySig> ClassifyStage() =>
        new Predict<ClassifySig>(_ctx with { Adapter = new XMLAdapter(XMLAdapter.ParseMode.Strict) });

    IModule<ClassifySig, DraftSig> DraftStage(DraftRoute route)
    {
        var composed = Step.OfPrediction<ClassifySig, DraftSig>(async (c, ct) =>
        {
            var strategy = StrategyFor(c);
            route.Strategy = strategy;
            return await _drafting.For(strategy).ForwardAsync(await InputAsync(c, ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
        });
        var plain = Step.OfPrediction<ClassifySig, DraftSig>(async (c, ct) =>
            await _drafting.Plain().ForwardAsync(await InputAsync(c, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
        var deservesEffort = Step.Of<ClassifySig, bool>("ImpactCheck", (c, _) =>
            Task.FromResult(c.Impact >= _options.ComposedImpact));

        var branch = new ConditionalBranch<ClassifySig, DraftSig, DraftSig>(deservesEffort, composed, plain);
        return Step.Of<ClassifySig, DraftSig>("DraftBranch", async (c, ct) =>
        {
            var chosen = await branch.ForwardAsync(c, ct).ConfigureAwait(false);
            route.Name = Equals(chosen.Trace.Metadata[SignalGrid.Ampelum.Composition.TraceKeys.BranchRoute], "ifTrue")
                ? ComposedRoute
                : PlainRoute;
            return (DraftSig)chosen.Value;
        });
    }

    DraftStrategy StrategyFor(ClassifySig c) =>
        _options.Strategies.TryGetValue(KindParser.Parse(c.Kind), out var strategy) ? strategy : DraftStrategy.BestOfN;

    async Task<DraftSig> InputAsync(ClassifySig c, CancellationToken ct) => new()
    {
        Title = c.Title,
        Details = c.Details,
        Kind = c.Kind,
        Impact = c.Impact,
        StyleNotes = await _style.NotesForAsync(c.Title, c.Details, ct).ConfigureAwait(false),
    };

    IModule<DraftSig, LoopResult<TightenSig>> TightenModule(DraftRoute route) =>
        Step.Of<DraftSig, LoopResult<TightenSig>>("Tighten", async (draft, ct) =>
        {
            var start = new TightenSig
            {
                Title = draft.Title,
                Draft = draft.Note,
                Kind = draft.Kind,
                Impact = draft.Impact,
                MaxWords = _options.MaxWords,
            };
            var (result, bodyRuns) = await _tighten.RunAsync(start, ct: ct).ConfigureAwait(false);
            route.TightenRuns = bodyRuns;
            return result;
        });

    IModule<LoopResult<TightenSig>, DraftedNote> ScoreStage(ChangeRow row, DraftRoute route) =>
        Step.Of<LoopResult<TightenSig>, DraftedNote>("Score", (loop, _) =>
        {
            var final = loop.FinalState;
            var meta = SignatureMetadataCache.For<DraftSig>();
            var quality = _drafting.Quality.Score(
                new DraftSig { Title = final.Title, Details = row.Details, Note = final.Draft },
                new DraftSig { Title = row.Title, Details = row.Details }, meta);
            return Task.FromResult(new DraftedNote(
                row.Id, row.Area, row.Title, KindParser.Parse(final.Kind), final.Impact, final.Draft,
                final.Words, route.TightenRuns, loop.Reason, quality));
        });
}

public static class KindParser
{
    public static ChangeKind Parse(string raw) =>
        Enum.TryParse<ChangeKind>(raw.Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : throw new InvalidOperationException($"Model returned '{raw}', which is not a valid {nameof(ChangeKind)}.");
}

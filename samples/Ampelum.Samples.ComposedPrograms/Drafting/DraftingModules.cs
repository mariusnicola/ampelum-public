using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;

namespace Ampelum.Samples.ComposedPrograms.Drafting;

/// <summary>
/// The drafting programs. Every one of them is wrapped the same way: the tone from the options is set
/// on the input, and a failed call is retried and then replaced by a note built from the title.
/// </summary>
public sealed class DraftingModules(AmpelumContext ctx, ReleaseNotesOptions options, ILogger logger)
{
    public NoteQuality Quality { get; } = new(options.MaxWords);

    /// <summary>One chain-of-thought call; used for changes that do not deserve more effort.</summary>
    public IModule<DraftSig, DraftSig> Plain()
    {
        var thinking = new ChainOfThought<DraftSig>(ctx);
        return Guard(Step.OfPrediction<DraftSig, DraftSig>((input, ct) => thinking.ForwardAsync(input, null, ct)));
    }

    public IModule<DraftSig, DraftSig> For(DraftStrategy strategy) => Guard(strategy switch
    {
        DraftStrategy.BestOfN => new BestOfN<DraftSig>(ctx, Quality, options.Candidates),
        DraftStrategy.Refine => new Refine<DraftSig>(ctx, Quality, options.RefineThreshold, options.MaxRefinements),
        DraftStrategy.MultiChainComparison =>
            new MultiChainComparison<DraftSig>(ctx, options.Candidates, AggregatorStrategy.ModelSynthesize),
        DraftStrategy.Audiences => new Parallel<DraftSig>(ctx, AudienceDrafters()),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown strategy."),
    });

    /// <summary>One drafter per audience; each differs only in the instructions sent with its call.</summary>
    IReadOnlyList<Predict<DraftSig>> AudienceDrafters() =>
        options.Audiences
            .Select(audience => new Predict<DraftSig>(ctx with { DefaultChatOptions = new ChatOptions { Instructions = audience } }))
            .ToList();

    IModule<DraftSig, DraftSig> Guard(IModule<DraftSig, DraftSig> inner)
    {
        var configured = new WithConfig<DraftSig>(inner, input => input.Tone = options.Tone);
        return new ErrorHandler<DraftSig>(configured, new ErrorHandler<DraftSig>.ErrorHandlerConfig
        {
            MaxRetries = options.DraftRetries,
            RetryDelay = options.RetryDelay,
            OnRetry = (error, attempt) => logger.LogWarning(error, "Draft call failed, retry {Attempt}", attempt),
            FallbackOutput = input => input with { Note = input.Title },
        });
    }
}

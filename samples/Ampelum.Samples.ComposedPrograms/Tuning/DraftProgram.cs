using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.ComposedPrograms.Tuning;

/// <summary>
/// The drafting call as a program an optimizer can tune. It implements <see cref="IComposedModule"/>
/// because only named submodules receive instruction overrides and demos when a tuned program is bound.
/// </summary>
public sealed class DraftProgram : IComposedModule, IModule<DraftSig, DraftSig>
{
    public const string DrafterName = "draft";

    /// <summary>An unbound program; <c>OptimizedProgram.Bind</c> supplies the real context.</summary>
    public DraftProgram() : this(Predict<DraftSig>.FromAmbient())
    {
    }

    public DraftProgram(Predict<DraftSig> drafter) => Drafter = drafter;

    public Predict<DraftSig> Drafter { get; }

    public IReadOnlyDictionary<string, object> Submodules =>
        new Dictionary<string, object> { [DrafterName] = Drafter };

    public object Rebind(IReadOnlyDictionary<string, object> newSubmodules) =>
        new DraftProgram((Predict<DraftSig>)newSubmodules[DrafterName]);

    /// <summary>Runs the drafting call.</summary>
    public Task<Prediction<DraftSig>> ForwardAsync(DraftSig inputs, CancellationToken ct = default) =>
        Drafter.ForwardAsync(inputs, ct: ct);

    public static DraftProgram Unbound(AmpelumContext context) =>
        new OptimizedProgram<DraftProgram>(
                new DraftProgram(), new Dictionary<string, IReadOnlyList<Example>>(), new Dictionary<string, string>())
            .Bind(context);
}

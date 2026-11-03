using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.ComposedPrograms.Tuning;

/// <summary>
/// One drafting call per kind of change, as a program GEPA can tune. Each kind has its own named submodule, so
/// an instruction that helps one kind changes the notes of that kind only. That is what gives GEPA
/// candidates that are best on different examples, and siblings that changed different submodules to merge.
/// </summary>
public sealed class KindRoutedProgram : IComposedModule
{
    readonly IReadOnlyDictionary<ChangeKind, Predict<DraftSig>> _drafters;

    /// <summary>An unbound program; <c>OptimizedProgram.Bind</c> supplies the real context.</summary>
    public KindRoutedProgram()
        : this(Enum.GetValues<ChangeKind>().ToDictionary(k => k, _ => Predict<DraftSig>.FromAmbient()))
    {
    }

    public KindRoutedProgram(IReadOnlyDictionary<ChangeKind, Predict<DraftSig>> drafters) => _drafters = drafters;

    public static string NameOf(ChangeKind kind) => kind.ToString().ToLowerInvariant();

    public IReadOnlyDictionary<string, object> Submodules =>
        _drafters.ToDictionary(d => NameOf(d.Key), d => (object)d.Value);

    public object Rebind(IReadOnlyDictionary<string, object> newSubmodules) =>
        new KindRoutedProgram(_drafters.ToDictionary(d => d.Key, d => (Predict<DraftSig>)newSubmodules[NameOf(d.Key)]));

    /// <summary>Drafts the note with the submodule of the change's kind.</summary>
    public Task<Prediction<DraftSig>> ForwardAsync(DraftSig input, CancellationToken ct = default) =>
        _drafters[Enum.Parse<ChangeKind>(input.Kind, ignoreCase: true)].ForwardAsync(input, ct: ct);

    public static KindRoutedProgram Unbound(AmpelumContext context) =>
        new OptimizedProgram<KindRoutedProgram>(
                new KindRoutedProgram(), new Dictionary<string, IReadOnlyList<Example>>(), new Dictionary<string, string>())
            .Bind(context);
}

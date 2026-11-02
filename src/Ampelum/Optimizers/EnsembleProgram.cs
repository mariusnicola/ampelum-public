namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// A composed program that holds multiple member programs and a reducer delegate.
/// Produced by <see cref="Ensemble{TProgram}.CompileAsync"/>.
/// </summary>
/// <typeparam name="TProgram">The member program type.</typeparam>
public sealed class EnsembleProgram<TProgram>
    where TProgram : class
{
    /// <summary>The wrapped member programs.</summary>
    public IReadOnlyList<TProgram> Members { get; }

    /// <summary>Delegate that reduces per-member predictions into a single output Example.</summary>
    public Func<IReadOnlyList<Example>, AmpelumContext, Example> Reducer { get; }

    /// <summary>Construct an ensemble program from a non-empty member list and a reducer.</summary>
    public EnsembleProgram(
        IReadOnlyList<TProgram> members,
        Func<IReadOnlyList<Example>, AmpelumContext, Example> reducer)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(reducer);
        Members = members;
        Reducer = reducer;
    }
}

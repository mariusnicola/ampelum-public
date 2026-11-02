namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Standalone composition builder that wraps multiple <typeparamref name="TProgram"/> instances
/// into a single <see cref="EnsembleProgram{TProgram}"/>.
///
/// <para><b>Why not <see cref="IOptimizer{TProgram}"/>?</b><br/>
/// Per spec Â§7.2.5c, <c>IOptimizer&lt;TProgram&gt;.CompileAsync</c> returns
/// <c>Task&lt;OptimizedProgram&lt;TProgram&gt;&gt;</c>. <see cref="Ensemble{TProgram}"/> returns
/// <c>Task&lt;OptimizedProgram&lt;EnsembleProgram&lt;TProgram&gt;&gt;&gt;</c> â€” a distinct
/// wrapper type. C# generics cannot satisfy this covariance for class types (PSQ-4).</para>
/// </summary>
/// <typeparam name="TProgram">The member program type.</typeparam>
public sealed class Ensemble<TProgram>
    where TProgram : class
{
    readonly Func<IReadOnlyList<Example>, AmpelumContext, Example> _reducer;

    /// <summary>Construct an ensemble builder with the given reducer.</summary>
    public Ensemble(Func<IReadOnlyList<Example>, AmpelumContext, Example> reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        _reducer = reducer;
    }

    /// <summary>Wrap <paramref name="programs"/> into an <see cref="EnsembleProgram{TProgram}"/>.</summary>
    public Task<OptimizedProgram<EnsembleProgram<TProgram>>> CompileAsync(
        IReadOnlyList<TProgram> programs,
        AmpelumContext ctx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(ctx);
        if (programs.Count == 0)
            throw new ArgumentException(
                "At least one program is required.", nameof(programs));

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new OptimizedProgram<EnsembleProgram<TProgram>>(
            Program: new EnsembleProgram<TProgram>(programs, _reducer),
            DemosBySubmodule: new Dictionary<string, IReadOnlyList<Example>>(),
            InstructionOverrides: new Dictionary<string, string>())
        {
            TrainsetSize = programs.Count,
        });
    }
}

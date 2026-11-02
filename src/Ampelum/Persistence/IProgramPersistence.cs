using SignalGrid.Ampelum.Optimizers;

namespace SignalGrid.Ampelum.Persistence;

/// <summary>Saves and loads <see cref="OptimizedProgram{TProgram}"/> instances.</summary>
public interface IProgramPersistence
{
    /// <summary>Serialises <paramref name="optimizedProgram"/> to <paramref name="path"/> atomically.</summary>
    Task SaveAsync<TProgram>(
        OptimizedProgram<TProgram> optimizedProgram,
        string path,
        CancellationToken ct = default)
        where TProgram : class;

    /// <summary>Deserialises an <see cref="OptimizedProgram{TProgram}"/> from <paramref name="path"/>.</summary>
    /// <exception cref="ProgramPersistenceException">I/O error, parse failure, or version mismatch.</exception>
    Task<OptimizedProgram<TProgram>> LoadAsync<TProgram>(
        string path,
        CancellationToken ct = default)
        where TProgram : class, new();
}

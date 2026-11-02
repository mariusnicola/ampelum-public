using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum.Persistence;

/// <summary>
/// Thrown when saving or loading an
/// <see cref="Ampelum.Optimizers.OptimizedProgram{TProgram}"/> fails due to I/O errors,
/// JSON parse failures, or version mismatches.
/// </summary>
public sealed class ProgramPersistenceException : AmpelumException
{
    /// <summary>Construct with a message.</summary>
    public ProgramPersistenceException(string message) : base(message) { }

    /// <summary>Construct with a message and inner exception.</summary>
    public ProgramPersistenceException(string message, Exception? inner) : base(message, inner) { }
}

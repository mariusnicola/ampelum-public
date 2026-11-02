using System.Runtime.CompilerServices;

namespace SignalGrid.Ampelum.Streaming;

/// <summary>
/// Contract implemented by a composed program whose primary output field is streamable via
/// <see cref="ProgramStreamer.Streamify{TProgram, TOut}"/>.
/// </summary>
public interface IStreamableProgram<TOut>
{
    /// <summary>
    /// Yields a stream of <see cref="StreamUpdate{TOut}"/> records representing the gradual construction of the output field.
    /// </summary>
    /// <param name="ct">Cancellation token. If signaled, the enumeration should cease and throw <see cref="OperationCanceledException"/>.</param>
    /// <returns>An async enumerable of stream updates, ending with a final update where <see cref="StreamUpdate{TOut}.IsFinal"/> is true.</returns>
    IAsyncEnumerable<StreamUpdate<TOut>> StreamAsync(CancellationToken ct);
}

/// <summary>
/// Entry point for program-level streaming. See spec Â§5.4 for invariants.
/// </summary>
/// <remarks>
/// Spec Â§5.4.1 shows <c>Streamify</c> accepting <c>Func&lt;TProgram, TOut&gt; fieldSelector</c>;
/// this implementation takes a <c>string fieldName</c> instead, because a runtime <c>Func</c>
/// cannot be introspected to recover the selected field name without expression trees.
/// Flagged for spec revision in v0.4 planning.
/// </remarks>
public static class ProgramStreamer
{
    /// <summary>
    /// Executes a program's streaming method and yields all <see cref="StreamUpdate{TOut}"/> records.
    /// </summary>
    /// <typeparam name="TProgram">The program type, must implement <see cref="IStreamableProgram{TOut}"/>.</typeparam>
    /// <typeparam name="TOut">The output type of the primary streamed field.</typeparam>
    /// <param name="program">The program instance to stream. Must not be null.</param>
    /// <param name="fieldName">The name of the field being streamed, recorded in activity tags. Must not be null.</param>
    /// <param name="ct">Cancellation token. When signaled, propagates to the underlying stream and stops iteration.</param>
    /// <returns>An async enumerable of stream updates from the program.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="program"/> or <paramref name="fieldName"/> is null.</exception>
    /// <remarks>
    /// Emits a <c>ampelum.program.stream</c> activity with tags: <c>program.type</c> (the type name of <typeparamref name="TProgram"/>) and
    /// <c>stream.field_name</c> (<paramref name="fieldName"/>).
    ///
    /// Spec Â§5.4.1 shows <c>Streamify</c> accepting a <c>Func&lt;TProgram, TOut&gt; fieldSelector</c>;
    /// this implementation takes a <c>string fieldName</c> instead, because a runtime <c>Func</c>
    /// cannot be introspected to recover the selected field name without expression trees.
    /// Flagged for spec revision in v0.4 planning.
    /// </remarks>
    public static async IAsyncEnumerable<StreamUpdate<TOut>> Streamify<TProgram, TOut>(
        TProgram program,
        string fieldName,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TProgram : IStreamableProgram<TOut>
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(fieldName);

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.program.stream");
        activity?.SetTag("program.type", typeof(TProgram).Name);
        activity?.SetTag("stream.field_name", fieldName);

        await foreach (var update in program.StreamAsync(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
